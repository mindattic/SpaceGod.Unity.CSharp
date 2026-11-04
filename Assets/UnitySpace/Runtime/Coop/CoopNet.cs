// US-T39: co-op's network link and player loop in Unity (new file, SpaceGod.Unity.CSharp; docs/PARITY.md "Co-op"). The
// Unreal C# track carried co-op's messages on a replicated link actor's four RPCs (ASpaceCoopLink) and ran a client's
// frames from that actor's Tick; here the same messages go over one TCP connection per client (System.Net.Sockets, a
// background reader per socket and a main-thread queue), and a MonoBehaviour does what the link actor and the game mode
// did. Everything above the link (CoopPeer, the frames, the syncs, the digests) is the Unreal track's code unchanged.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Unity.Shared.Core;
using Unity.SpaceGod.Input;
using UnityEngine;

namespace Unity.SpaceGod.Coop
{
    /// <summary>
    /// One end of a co-op connection over TCP: each message is a 4-byte little-endian length and that many UTF-8 bytes,
    /// reliable and ordered (an unreliable send goes the same way: TCP has no other). A background thread reads; the main
    /// thread drains <see cref="Inbox"/> once a frame, so CoopPeer only ever runs on the main thread, as under Unreal.
    /// </summary>
    public sealed class CoopTcpLink : ICoopLink
    {
        public const int MaxMessage = 64 * 1024 * 1024;

        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly object writeLock = new object();
        public readonly ConcurrentQueue<string> Inbox = new ConcurrentQueue<string>();
        volatile bool closed;

        public string Remote { get; }
        public bool Closed => closed;

        public CoopTcpLink(TcpClient client)
        {
            this.client = client;
            client.NoDelay = true;
            stream = client.GetStream();
            Remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "SpaceGodCoop read " + Remote };
            reader.Start();
        }

        public void Send(string message)
        {
            if (closed) return;
            byte[] body = Encoding.UTF8.GetBytes(message ?? "");
            byte[] head = BitConverter.GetBytes(body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(head);
            try
            {
                lock (writeLock)
                {
                    stream.Write(head, 0, 4);
                    stream.Write(body, 0, body.Length);
                }
            }
            catch (Exception) { Close(); }
        }

        public void SendUnreliable(string message) => Send(message);

        void ReadLoop()
        {
            var head = new byte[4];
            try
            {
                while (!closed)
                {
                    if (!ReadExactly(head, 4)) break;
                    if (!BitConverter.IsLittleEndian) Array.Reverse(head);
                    int length = BitConverter.ToInt32(head, 0);
                    if (length < 0 || length > MaxMessage) break;
                    var body = new byte[length];
                    if (!ReadExactly(body, length)) break;
                    Inbox.Enqueue(Encoding.UTF8.GetString(body));
                }
            }
            catch (Exception) { }
            Close();
        }

        bool ReadExactly(byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { client.Close(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Co-op's player loop in Unity (US-T39), what the Unreal track's game mode (ASpaceGameMode) and link actor
    /// (ASpaceCoopLink) did there. <c>-CoopHost</c> listens on <c>-port=</c> (default <see cref="DefaultPort"/>, Unreal's)
    /// and hosts as player 1: every client that connects gets a link, its messages go to CoopPeer before the app's Update,
    /// and after every Update the frame goes out. <c>-CoopJoin=&lt;address[:port]&gt;</c> connects and goes straight into the
    /// game; the client's app runs only the host's frames, replayed here in order (up to <see cref="MaxFramesPerTick"/> a
    /// Unity frame, so a client that fell behind catches up), never on its own clock.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class CoopNet : MonoBehaviour
    {
        public const int DefaultPort = 7777;
        const int MaxFramesPerTick = 8;

        static CoopNet instance;
        TcpListener listener;
        readonly ConcurrentQueue<TcpClient> accepted = new ConcurrentQueue<TcpClient>();
        readonly List<CoopTcpLink> links = new List<CoopTcpLink>();
        CoopTcpLink hostLink;
        bool helloSent, smokeEquipped;

        /// <summary>Before the first scene: a co-op session from the command line, so the main menu is skipped for a client
        /// (GameLaunch.SkipsMenu) and the profile folders are set before any profile is read.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void FromCommandLine()
        {
            string joinAt = Bootstrap.CommandLineValue("-CoopJoin");
            bool host = Bootstrap.HasCommandLineFlag("-CoopHost");
            if (string.IsNullOrEmpty(joinAt) && !host) return;
            try
            {
                if (!string.IsNullOrEmpty(joinAt)) StartClient(joinAt);
                else StartHost();
            }
            catch (Exception e)
            {
                CoopPeer.Warn("could not start co-op: " + e.Message);
                CoopPeer.Stop();
            }
        }

        static string ProfilesRoot(string side)
        {
            // A co-op client's autosaves are of the host's world: a profile of its own, never the player's single-player
            // saves; the smoke run's both sides in a scratch folder (the Unreal track's Saved/SmokeProfiles).
            var root = CoopSmoke.Active
                ? Path.Combine(Application.persistentDataPath, "SmokeProfiles", side)
                : Path.Combine(Application.persistentDataPath, "Coop" + side);
            Directory.CreateDirectory(root);
            return root;
        }

        static int Port(string joinAt, out string address)
        {
            address = joinAt;
            int port = DefaultPort;
            string p = Bootstrap.CommandLineValue("-port");
            if (p != null && int.TryParse(p, out int fromFlag)) port = fromFlag;
            if (joinAt != null)
            {
                int colon = joinAt.LastIndexOf(':');
                if (colon > 0 && int.TryParse(joinAt.Substring(colon + 1), out int fromAddress))
                {
                    port = fromAddress;
                    address = joinAt.Substring(0, colon);
                }
            }
            return port;
        }

        static CoopNet Make()
        {
            var go = new GameObject("SpaceGodCoop");
            DontDestroyOnLoad(go);
            return instance = go.AddComponent<CoopNet>();
        }

        static void StartHost()
        {
            if (CoopSmoke.Active) Profiles.UseRoot(ProfilesRoot("CoopHost"));
            CoopPeer.StartHost(CoopPeer.NameFromCommandLine(CoopSmoke.Active ? "Host" : null));
            int port = Port(null, out _);
            var net = Make();
            net.listener = new TcpListener(IPAddress.Any, port);
            net.listener.Start();
            var accept = new Thread(net.AcceptLoop) { IsBackground = true, Name = "SpaceGodCoop accept" };
            accept.Start();
            CoopPeer.Log("listening on port " + port + " (TCP)");
        }

        static void StartClient(string joinAt)
        {
            Profiles.UseRoot(ProfilesRoot("CoopClient"));
            int port = Port(joinAt, out string address);
            var tcp = new TcpClient();
            var watch = Stopwatch.StartNew();
            Exception last = null;
            // The host may still be starting: try for a while, as Unreal's connection does.
            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                try { tcp.Connect(address, port); last = null; break; }
                catch (Exception e) { last = e; Thread.Sleep(500); tcp.Close(); tcp = new TcpClient(); }
            }
            if (last != null) throw new IOException("no host at " + address + ":" + port + ": " + last.Message);
            var link = new CoopTcpLink(tcp);
            CoopPeer.StartClient(link, CoopPeer.NameFromCommandLine(CoopSmoke.Active ? "Client" : null));
            Make().hostLink = link;
            CoopPeer.Log("connected: link to the host is up (" + address + ":" + port + ")");
        }

        void AcceptLoop()
        {
            try
            {
                while (true) accepted.Enqueue(listener.AcceptTcpClient());
            }
            catch (Exception) { }
        }

        void Update()
        {
            var peer = CoopPeer.Current;
            if (peer == null) return;
            WireHud();
            if (peer.IsHost) HostUpdate(peer);
            else ClientUpdate(peer);
        }

        // ---------------------------------------------------------------- the host

        void HostUpdate(CoopPeer peer)
        {
            while (accepted.TryDequeue(out var tcp))
            {
                var link = new CoopTcpLink(tcp);
                links.Add(link);
                peer.HostLogin(link);
                CoopPeer.Log("a player logged in from " + link.Remote + "; link up");
            }
            for (int i = links.Count - 1; i >= 0; i--)
            {
                var link = links[i];
                while (link.Inbox.TryDequeue(out var msg)) peer.HostReceive(link, msg);
                if (link.Closed && link.Inbox.IsEmpty)
                {
                    links.RemoveAt(i);
                    peer.HostLogout(link);
                }
            }
            if (CoopSmoke.Active && !smokeEquipped && Bootstrap.App != null)
            {
                smokeEquipped = true;
                Application.targetFrameRate = 60; // the co-op smoke's pace
                var app = Bootstrap.App;
                CoopPeer.Log("host app up: " + app.Galaxy.Systems.Count + " systems, " + app.Fleet.Ships.Count + " ships");
            }
        }

        /// <summary>After every Update (the app's included): the host's frame goes to the clients.</summary>
        void LateUpdate()
        {
            var peer = CoopPeer.Current;
            if (peer != null && peer.IsHost) peer.HostEndUpdate(Bootstrap.App);
        }

        // ---------------------------------------------------------------- a client: the player loop (ASpaceCoopLink.Tick)

        void ClientUpdate(CoopPeer peer)
        {
            while (hostLink.Inbox.TryDequeue(out var msg)) peer.ClientReceive(msg);
            if (hostLink.Closed && hostLink.Inbox.IsEmpty)
            {
                CoopPeer.Warn("the host has gone");
                CoopPeer.Stop();
                if (CoopSmoke.Active) Application.Quit();
                else GameLaunch.ToMainMenu();
                return;
            }
            CoopPeer.EngineTick++; // US-T43: the device's edges belong to the tick, however many host frames it replays
            CoopSmoke.ClientTick(peer);
            var app = Bootstrap.App;
            if (app == null || peer.Refused) return;
            if (CoopSmoke.Active && !smokeEquipped)
            {
                smokeEquipped = true;
                app.Input.Device = CoopSmoke.Script = new ScriptedInputDevice();
                CoopPeer.EdgesPerTick = false; // US-T43: the script clears its edges after every Update
                Application.targetFrameRate = 60;
            }
            if (!helloSent)
            {
                helloSent = true;
                CoopPeer.Log("client app up; asking the host for a seat as " + peer.LocalName);
                peer.ClientHello();
            }
            if (!peer.Running && !peer.ClientTryLoad(app)) return; // waiting for the host's save
            for (int k = 0; k < MaxFramesPerTick; k++)
            {
                var rec = peer.NextFrame();
                if (rec == null) break;
                peer.ClientRecord = rec;
                peer.Consumed = false;
                app.CoopReplay();
                CoopSmoke.Script?.EndFrame();
                if (!peer.Consumed)
                {
                    // The Update stopped before the clock (the pause menu is open here): the frame waits for the next one.
                    peer.ClientRecord = null;
                    break;
                }
                app.CoopApplyPost(rec);
                peer.ClientFrameDone(app, rec);
                CoopSmoke.ClientFrame(peer, app);
                peer.ClientRecord = null;
                app = Bootstrap.App;
                if (app == null || !peer.Running) break;
            }
        }

        /// <summary>Every app made while co-op runs (a host's New Game too) gets the co-op HUD.</summary>
        static void WireHud()
        {
            var app = Bootstrap.App;
            if (app != null && app.GetComponent<CoopHud>() == null) app.gameObject.AddComponent<CoopHud>();
        }

        void OnApplicationQuit()
        {
            var peer = CoopPeer.Current;
            if (peer != null && peer.IsClient) peer.SayBye();
            foreach (var l in links) l.Close();
            hostLink?.Close();
            try { listener?.Stop(); } catch (Exception) { }
        }
    }
}
