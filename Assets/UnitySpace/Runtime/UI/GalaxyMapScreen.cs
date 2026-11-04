using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.SpaceGod.Core;
using UnityEngine;

namespace Unity.SpaceGod.UI
{
    /// <summary>
    /// The galaxy map (U.SPC-T103, [ or the navigator's GALAXY MAP): every simulated star system at its real position,
    /// seen from above the ecliptic and tilted to show depth, with the gate network between them. Each system is haloed
    /// in the colour of the polity that holds it; each gate link is drawn as thick as the traffic through it, with that
    /// traffic flowing along it; war fronts, shut gates, sieges and blockades are marked in red, raiders in orange, and
    /// gates being built dashed. All of it is what the player knows (<see cref="GalaxyMap"/>): the system they are in as
    /// it is, every other as the last news of it had it, and a system never heard from as a bare star.
    ///
    /// Click a star to inspect it (the panel on the right), double-click or TRAVEL to fly there through the gates (the
    /// navigator's STAR SYSTEMS trip, <see cref="UnitySpaceApp.TravelTo"/>). At the helm of a hull with its own FTL drive
    /// (U.SPC-T420) any star or beacon, the plain stars on the charts among them, can be picked to plot an FTL course to
    /// (its ETA, fuel and any well on the way) and the FTL autopilot engaged for it. Drag to turn the map, shift-drag to pan,
    /// the wheel to zoom. The view fills the screen; its panels keep to the middle 21:9 band, so on a 32:9 screen they
    /// sit where the eye is rather than out at the far edges.
    /// </summary>
    public sealed partial class GalaxyMapScreen // US-T47: partial (GalaxyMapScreen.Coop.cs)
    {
        readonly UnitySpaceApp app;
        GalaxyMapModel model;
        float builtAt = -10f;
        StarSystem builtFor;

        StarSystem selected, hovered;

        /// <summary>A plain star on the charts picked or under the cursor (U.SPC-T420): nobody simulates it, but an FTL drive can go there.</summary>
        NearbyStar? selectedStar, hoveredStar;

        /// <summary>The plain stars drawn, nearest the system the player is in first, gathered with the model.</summary>
        readonly List<NearbyStar> chartStars = new List<NearbyStar>();

        /// <summary>The most plain stars drawn: the charts run on far beyond what the map frames.</summary>
        const int MaxChartStars = 300;
        float yaw, tilt = 0.55f, zoom = 1f;
        Vector2 pan;
        bool fitted;
        float fitScale = 1f;
        DVec3 centreLy;

        bool showTerritory = true, showFlows = true, showFronts = true, showPirates = true, showConstruction = true;

        /// <summary>The strategy layer's COMMAND panel is up in place of the system detail (U.SPC-T423).</summary>
        bool commandMode;
        Faction commanded;
        Ship commandGroup;
        CommandScope commandScope = CommandScope.Group;
        Vector2 groupScroll;
        PolityReport report;
        float reportAt = -10f;

        /// <summary>The DIPLOMACY panel (the envoy screen, U.SPC-T427) is up in place of the system detail; the state treated with.</summary>
        bool diplomacyMode, warArmed;
        Faction envoyWith;
        Vector2 diplomacyScroll;
        float diplomacyHeight = 600;

        GUIStyle title, text, small, label, labelDim, button, buttonOn;
        Texture2D panelTex, buttonTex, buttonOnTex, glow;

        static readonly Color Accent = new Color(0.45f, 0.85f, 1f);
        static readonly Color Dim = new Color(0.62f, 0.7f, 0.78f);
        static readonly Color Unknown = new Color(0.5f, 0.54f, 0.6f);
        static readonly Color FlowColor = new Color(1f, 0.82f, 0.4f);
        static readonly Color FrontColor = new Color(1f, 0.3f, 0.26f);
        static readonly Color PirateColor = new Color(1f, 0.55f, 0.15f);
        static readonly Color BuildColor = new Color(0.75f, 0.8f, 0.9f);
        static readonly Color CourseColor = new Color(0.6f, 0.95f, 0.75f);

        /// <summary>The widest the panels spread, as width over height: 21:9, as the rest of the HUD.</summary>
        const float MaxPanelAspect = 21f / 9f;

        public GalaxyMapScreen(UnitySpaceApp app) => this.app = app;

        /// <summary>The middle band the panels keep to: the whole width up to 21:9, the middle 21:9 of anything wider.</summary>
        static float BandWidth => Mathf.Min(Screen.width, Screen.height * MaxPanelAspect);
        static float BandLeft => (Screen.width - BandWidth) * 0.5f;
        static float BandRight => BandLeft + BandWidth;

        /// <summary>Opened (again): starts on the system the player is in, framed to fit.</summary>
        public void Opened()
        {
            selected = app.ActiveSystem;
            selectedStar = null;
            fitted = false;
            builtAt = -10f;
        }

        public void Draw()
        {
            EnsureStyles();
            var screen = new Rect(0, 0, Screen.width, Screen.height);
            // The map has the whole screen: nothing under it is clicked or steered while it is up.
            if (Event.current.type == EventType.Layout || Event.current.type == EventType.Repaint)
                if (!app.UiRects.Contains(screen)) app.UiRects.Add(screen);

            double jd = app.Clock.Jd;
            if (model == null || builtFor != app.ActiveSystem || Time.unscaledTime - builtAt > 0.25f)
            {
                model = GalaxyMap.Build(app.Galaxy, app.ActiveSystem, jd, app.Galaxy.Construction?.Works());
                builtAt = Time.unscaledTime;
                builtFor = app.ActiveSystem;
                GatherChartStars();
            }
            if (selected == null || model.Of(selected) == null) selected = app.ActiveSystem;

            var old = GUI.color;
            GUI.color = new Color(0.01f, 0.015f, 0.03f, 0.9f);
            GUI.DrawTexture(screen, Texture2D.whiteTexture);
            GUI.color = old;

            const float legendW = 270, detailW = 360, headerH = 46;
            float left = BandLeft + 10, right = BandRight - 10;
            var header = new Rect(left, 10, right - left, headerH);
            var legend = new Rect(left, header.yMax + 8, legendW, Screen.height - header.yMax - 18);
            var detail = new Rect(right - detailW, header.yMax + 8, detailW, Screen.height - header.yMax - 18);
            var canvas = new Rect(legend.xMax + 8, header.yMax + 8, detail.xMin - legend.xMax - 16, Screen.height - header.yMax - 18);

            if (!fitted) Fit(canvas);
            HandleInput(canvas);
            if (Event.current.type == EventType.Repaint) DrawMap(canvas, jd);
            DrawHeader(header, jd);
            DrawLegend(legend);
            if (app.EnvoyTo != null)
            {
                // A hail from the helm (F12) opens the envoy on the polity hailed.
                envoyWith = app.EnvoyTo;
                app.EnvoyTo = null;
                diplomacyMode = true;
                commandMode = false;
                warArmed = false;
            }
            if (diplomacyMode) DrawDiplomacy(detail, jd);
            else if (commandMode) DrawCommand(detail, jd);
            else DrawDetail(detail, jd);
        }

        // ---------------------------------------------------------------- projection

        /// <summary>Frames every system: centred on their middle, scaled so the farthest fits the canvas.</summary>
        void Fit(Rect canvas)
        {
            var systems = app.Galaxy.Systems;
            var sum = DVec3.Zero;
            foreach (var s in systems) sum += s.PositionLy;
            centreLy = systems.Count > 0 ? sum / systems.Count : DVec3.Zero;
            double reach = 1;
            foreach (var s in systems) reach = Math.Max(reach, (s.PositionLy - centreLy).Length);
            fitScale = 0.46f * Mathf.Min(canvas.width, canvas.height) / (float)reach;
            // A wide canvas has room to spare across: use some of it.
            fitScale *= Mathf.Clamp(canvas.width / Mathf.Max(canvas.height, 1f), 1f, 1.6f);
            pan = Vector2.zero;
            zoom = 1;
            fitted = true;
        }

        /// <summary>A point (light-years) on the canvas: turned by the yaw about the ecliptic pole, tilted, scaled and panned.</summary>
        Vector2 Project(Rect canvas, DVec3 ly, out float depth)
        {
            var d = ly - centreLy;
            float c = Mathf.Cos(yaw), s = Mathf.Sin(yaw);
            float x = (float)(d.X * c - d.Y * s);
            float y = (float)(d.X * s + d.Y * c);
            float z = (float)d.Z;
            float up = y * Mathf.Cos(tilt) + z * Mathf.Sin(tilt);
            depth = -y * Mathf.Sin(tilt) + z * Mathf.Cos(tilt);
            float k = fitScale * zoom;
            return canvas.center + pan + new Vector2(x * k, -up * k);
        }

        Vector2 Project(Rect canvas, DVec3 ly) => Project(canvas, ly, out _);

        /// <summary>The same point dropped to the ecliptic plane (z = the map's middle): where a star's stalk stands.</summary>
        Vector2 Foot(Rect canvas, DVec3 ly) => Project(canvas, new DVec3(ly.X, ly.Y, centreLy.Z));

        /// <summary>The plain stars on the charts nearest the system the player is in, up to <see cref="MaxChartStars"/>.</summary>
        void GatherChartStars()
        {
            chartStars.Clear();
            var here = app.ActiveSystem.PositionLy;
            chartStars.AddRange(app.Galaxy.NearbyStars);
            chartStars.RemoveAll(n => n.Name == null);
            chartStars.Sort((a, b) => (a.PositionLy - here).LengthSquared.CompareTo((b.PositionLy - here).LengthSquared));
            if (chartStars.Count > MaxChartStars) chartStars.RemoveRange(MaxChartStars, chartStars.Count - MaxChartStars);
        }

        NearbyStar? NearestStar(Rect canvas, Vector2 mouse)
        {
            NearbyStar? best = null;
            float bestPx = 12;
            foreach (var n in chartStars)
            {
                float px = (Project(canvas, n.PositionLy) - mouse).magnitude;
                if (px < bestPx)
                {
                    bestPx = px;
                    best = n;
                }
            }
            return best;
        }

        StarSystem Nearest(Rect canvas, Vector2 mouse)
        {
            StarSystem best = null;
            float bestPx = 22;
            foreach (var m in model.Systems)
            {
                float px = (Project(canvas, m.System.PositionLy) - mouse).magnitude;
                if (px < bestPx)
                {
                    bestPx = px;
                    best = m.System;
                }
            }
            return best;
        }

        // ---------------------------------------------------------------- input

        void HandleInput(Rect canvas)
        {
            var e = Event.current;
            if (!canvas.Contains(e.mousePosition))
            {
                if (e.type == EventType.Repaint)
                {
                    hovered = null;
                    hoveredStar = null;
                }
                return;
            }
            if (e.type == EventType.MouseMove || e.type == EventType.Repaint)
            {
                hovered = Nearest(canvas, e.mousePosition);
                hoveredStar = hovered == null ? NearestStar(canvas, e.mousePosition) : null;
            }
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0:
                {
                    var hit = Nearest(canvas, e.mousePosition);
                    if (hit != null)
                    {
                        selected = hit;
                        selectedStar = null;
                        if (e.clickCount >= 2) Travel(hit);
                    }
                    else if (NearestStar(canvas, e.mousePosition) is NearbyStar star)
                    {
                        // A plain star: no gate goes there, but an FTL drive can (U.SPC-T420).
                        selectedStar = star;
                        if (e.clickCount >= 2 && app.CanPlotFtl) app.PlotFtlCourse(FtlTarget.OfStar(star));
                    }
                    e.Use();
                    break;
                }
                case EventType.MouseDrag when e.button == 0 || e.button == 2:
                    if (e.shift || e.button == 2) pan += e.delta;
                    else
                    {
                        yaw += e.delta.x * 0.008f;
                        tilt = Mathf.Clamp(tilt + e.delta.y * 0.008f, 0f, 1.45f);
                    }
                    e.Use();
                    break;
                case EventType.ScrollWheel:
                {
                    float before = zoom;
                    zoom = Mathf.Clamp(zoom * Mathf.Pow(1.12f, -e.delta.y / 3f), 0.35f, 8f);
                    // Zoom about the cursor: the point under it stays put.
                    var towards = e.mousePosition - (canvas.center + pan);
                    pan -= towards * (zoom / before - 1);
                    e.Use();
                    break;
                }
            }
        }

        void Travel(StarSystem to)
        {
            if (to == null || to == app.ActiveSystem) return;
            app.ShowGalaxyMap = false;
            app.TravelTo(to);
        }

        // ---------------------------------------------------------------- the map

        void DrawMap(Rect canvas, double jd)
        {
            GUI.BeginClip(canvas);
            var local = new Rect(0, 0, canvas.width, canvas.height);
            var focus = selected != null && selected != app.ActiveSystem ? selected : hovered;
            // Nothing picked: the helm's plotted course (U.SPC-T516).
            if ((focus == null || focus == app.ActiveSystem) && app.Course != null && !app.Course.Done) focus = app.Course.Destination;
            var route = focus != null && focus != app.ActiveSystem ? app.Galaxy.Network.Route(app.ActiveSystem, focus, null, jd) : null;
            var onRoute = new HashSet<GateLink>();
            if (route != null)
                foreach (var g in route)
                    onRoute.Add(g.Link);

            // Stalks first: each star's height above or below the plane, so the depth reads.
            foreach (var m in model.Systems)
            {
                var p = Project(local, m.System.PositionLy);
                var f = Foot(local, m.System.PositionLy);
                Line(f, p, new Color(Dim.r, Dim.g, Dim.b, 0.18f), 1f);
                Disc(f, 3, new Color(Dim.r, Dim.g, Dim.b, 0.25f));
            }

            // Territory: a wash of the holder's colour round each system it holds, every other holder's share a smaller one.
            if (showTerritory)
                foreach (var m in model.Systems)
                {
                    if (!m.Known || m.Holdings.Count == 0) continue;
                    var p = Project(local, m.System.PositionLy);
                    double total = 0;
                    foreach (var h in m.Holdings) total += h.Population;
                    for (int i = m.Holdings.Count - 1; i >= 0; i--)
                    {
                        var h = m.Holdings[i];
                        float share = total > 0 ? (float)(h.Population / total) : 1f / m.Holdings.Count;
                        float r = i == 0 ? 46 + 10 * Mathf.Log10(1 + (float)total / 1e6f) : 18 + 30 * Mathf.Sqrt(share);
                        Disc(p, r, Tint(h.Faction.Color, i == 0 ? 0.32f : 0.22f));
                    }
                }

            // The gate links.
            foreach (var l in model.Links)
            {
                var a = Project(local, l.A.PositionLy);
                var b = Project(local, l.B.PositionLy);
                if (l.UnderConstruction)
                {
                    if (!showConstruction) continue;
                    Dashed(a, b, new Color(BuildColor.r, BuildColor.g, BuildColor.b, 0.55f), 1.5f, 6, 6);
                    var mid = Vector2.Lerp(a, b, 0.5f);
                    Line(a, Vector2.Lerp(a, b, (float)l.Progress), new Color(BuildColor.r, BuildColor.g, BuildColor.b, 0.9f), 2f);
                    Label(mid + new Vector2(6, -16), "building " + (l.Progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%", BuildColor, true);
                    continue;
                }
                bool routed = l.Link != null && onRoute.Contains(l.Link);
                if (!l.Known) Dashed(a, b, new Color(Unknown.r, Unknown.g, Unknown.b, 0.4f), 1f, 4, 6);
                else Line(a, b, new Color(0.45f, 0.75f, 0.95f, 0.45f), 1.4f);
                if (showFlows && l.Crossings > 0)
                {
                    float w = 2 + 7 * (float)l.Flow;
                    Line(a, b, new Color(FlowColor.r, FlowColor.g, FlowColor.b, 0.28f), w);
                    FlowDots(a, b, l);
                }
                if (routed) Line(a, b, new Color(Accent.r, Accent.g, Accent.b, 0.95f), 3f);
                if (showFronts && l.WarFront) Dashed(a, b, FrontColor, 3.5f, 10, 6);
                if (showFronts && l.Shut)
                {
                    // A bar across the line at the shut end.
                    if (l.ShutAtA) Barrier(a, b);
                    if (l.ShutAtB) Barrier(b, a);
                }
            }

            // The plain stars on the charts (U.SPC-T420): no system yet, but an FTL drive goes anywhere.
            foreach (var n in chartStars)
            {
                var p = Project(local, n.PositionLy);
                if (!local.Contains(p)) continue;
                var c = Color.Lerp(new Color(n.Color.R, n.Color.G, n.Color.B), Unknown, 0.45f);
                bool picked = selectedStar.HasValue && selectedStar.Value.Name == n.Name;
                bool hover = hoveredStar.HasValue && hoveredStar.Value.Name == n.Name;
                Disc(p, picked || hover ? 4.5f : 2.6f, new Color(c.r, c.g, c.b, picked || hover ? 0.95f : 0.6f));
                if (picked) Brackets(p, 11, Accent);
                if (picked || hover) Label(p + new Vector2(8, -14), n.Name + "  ·  " + n.SpectralType, picked ? Accent : Dim, true);
            }

            // The plotted FTL course (U.SPC-T420): a straight line through open space, unlike any gate route.
            var course = app.PlottedCourse;
            if (course?.Target != null && course.From == app.ActiveSystem)
            {
                var a = Project(local, app.ActiveSystem.PositionLy);
                var b = Project(local, course.Target.Origin.Ly);
                var c = course.Flyable ? CourseColor : FrontColor;
                Dashed(a, b, new Color(c.r, c.g, c.b, 0.9f), 2.5f, 12, 5);
                Ring(b, 15, c, 2f);
                Label(Vector2.Lerp(a, b, 0.5f) + new Vector2(8, 4), "FTL " + course.Ly.ToString("0.00", CultureInfo.InvariantCulture) + " ly" +
                      (course.Flyable ? ", ETA " + FtlNavigation.Describe(course.TotalDays) : ""), c, true);
            }

            // The stars.
            foreach (var m in model.Systems)
            {
                var p = Project(local, m.System.PositionLy);
                var c = StarColor(m.System);
                if (!m.Known && !m.Here) c = Color.Lerp(c, Unknown, 0.6f);
                Disc(p, 9, new Color(c.r, c.g, c.b, 0.35f));
                Disc(p, 4.5f, c);
                if (m.Here) Ring(p, 13, Accent, 2f);
                if (m.System == selected) Brackets(p, 17, Accent);
                else if (m.System == hovered) Brackets(p, 15, new Color(1, 1, 1, 0.6f));
                if (showFronts && (m.Contested || m.Threats.Count > 0)) Ring(p, 20 + 2 * Mathf.Sin(Time.unscaledTime * 4), FrontColor, 2f);
                if (showPirates && m.Pirates != PirateActivity.None)
                {
                    float size = m.Pirates == PirateActivity.High ? 15 : 11;
                    var at = p + new Vector2(-20, 12);
                    var old = GUI.color;
                    GUI.color = new Color(PirateColor.r, PirateColor.g, PirateColor.b, m.Pirates == PirateActivity.High ? 0.75f + 0.25f * Mathf.Sin(Time.unscaledTime * 5) : 0.8f);
                    GUI.DrawTexture(new Rect(at.x - size * 0.5f, at.y - size * 0.5f, size, size), Icons.ForShip(ShipClass.Destroyer));
                    GUI.color = old;
                }

                string name = m.System.Name == "Sol System" ? "SOL" : m.System.Name.ToUpperInvariant();
                Label(p + new Vector2(12, -20), name, m.Here ? Accent : m.Known ? Color.white : Unknown, false);
                string sub = m.Here ? "you are here"
                    : !m.Known ? "no news"
                    : (m.Holder != null ? m.Holder.Name + "  ·  " : "unclaimed  ·  ") + Age(m.AgeDays);
                Label(p + new Vector2(12, -5), sub, m.Holder != null && showTerritory ? Tint(m.Holder.Color, 0.95f) : Dim, true);
            }
            GUI.EndClip();
        }

        /// <summary>Sparks along a link, as many as its traffic, each way in proportion to the crossings that way.</summary>
        void FlowDots(Vector2 a, Vector2 b, MapLink l)
        {
            int n = 2 + Mathf.RoundToInt(5 * (float)l.Flow);
            int towardB = Mathf.RoundToInt(n * (l.Crossings > 0 ? (float)l.TowardB / l.Crossings : 0.5f));
            float t = Time.unscaledTime * 0.12f;
            for (int i = 0; i < n; i++)
            {
                float f = Mathf.Repeat(t + i / (float)n + (l.A.Name.Length * 0.137f), 1f);
                var p = i < towardB ? Vector2.Lerp(a, b, f) : Vector2.Lerp(b, a, f);
                Disc(p, 2.6f, FlowColor);
            }
        }

        void Barrier(Vector2 at, Vector2 toward)
        {
            var d = (toward - at).normalized;
            var p = at + d * 22;
            var n = new Vector2(-d.y, d.x);
            Line(p - n * 8, p + n * 8, FrontColor, 3f);
        }

        static Color StarColor(StarSystem s)
        {
            var root = s.Catalog.Root;
            foreach (var b in s.Catalog.Bodies)
                if (b.IsStar)
                {
                    root = b;
                    break;
                }
            var c = new Color(root.Color.R, root.Color.G, root.Color.B);
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            c = max > 0 ? c / max : Color.white;
            return Color.Lerp(c, Color.white, 0.35f);
        }

        static Color Tint(Rgb c, float a) => new Color(c.R, c.G, c.B, a);

        static string Age(double days) => double.IsNaN(days) ? "no news" : days <= 0 ? "live" : "news " + LightSpeed.Describe(days) + " old";

        // ---------------------------------------------------------------- panels

        void DrawHeader(Rect r, double jd)
        {
            GUI.Box(r, GUIContent.none, Panel);
            GUI.Label(new Rect(r.x + 14, r.y + 7, 220, 20), "GALAXY MAP", title);
            int known = 0;
            foreach (var m in model.Systems)
                if (m.Known) known++;
            var old = GUI.contentColor;
            GUI.contentColor = Dim;
            GUI.Label(new Rect(r.x + 14, r.y + 26, r.width - 530, 16),
                "As known in " + app.ActiveSystem.Name + "   ·   " + known + " of " + model.Systems.Count + " systems heard from   ·   " +
                model.Links.Count + " gate links   ·   traffic over the last " + GalaxyMap.FlowWindowDays.ToString("0", CultureInfo.InvariantCulture) + " days", small);
            GUI.contentColor = old;
            if (GUI.Button(new Rect(r.xMax - 470, r.y + 10, 120, 26), diplomacyMode ? "SYSTEM DETAIL" : "DIPLOMACY", diplomacyMode ? buttonOn : button))
            {
                diplomacyMode = !diplomacyMode;
                commandMode = false;
                warArmed = false;
            }
            if (GUI.Button(new Rect(r.xMax - 340, r.y + 10, 120, 26), commandMode ? "SYSTEM DETAIL" : "COMMAND", commandMode ? buttonOn : button))
            {
                commandMode = !commandMode;
                diplomacyMode = false;
                reportAt = -10f;
            }
            if (GUI.Button(new Rect(r.xMax - 210, r.y + 10, 120, 26), "Reset view", button)) fitted = false;
            if (GUI.Button(new Rect(r.xMax - 82, r.y + 10, 72, 26), "Close", button)) app.ShowGalaxyMap = false;
        }

        void DrawLegend(Rect r)
        {
            GUI.Box(r, GUIContent.none, Panel);
            float y = r.y + 10;
            float x = r.x + 12, w = r.width - 24;
            Heading(ref y, x, w, "LAYERS");
            Toggle(ref y, x, w, ref showTerritory, "Territory", "each system washed in its holder's colour");
            Toggle(ref y, x, w, ref showFlows, "Traffic", "gate crossings: thicker is busier");
            Toggle(ref y, x, w, ref showFronts, "Fronts & blockades", "war fronts, shut gates, sieges");
            Toggle(ref y, x, w, ref showPirates, "Raiders", "raider hulls, havens and raids");
            Toggle(ref y, x, w, ref showConstruction, "Gates being built", "dashed, filled as far as built");
            y += 6;

            Heading(ref y, x, w, "KEY");
            Swatch(ref y, x, w, new Color(0.45f, 0.75f, 0.95f), "gate link", false);
            Swatch(ref y, x, w, Unknown, "link to a system never heard from", true);
            Swatch(ref y, x, w, FlowColor, "traffic through the gate", false);
            Swatch(ref y, x, w, Accent, "the way there by the gates", false);
            Swatch(ref y, x, w, CourseColor, "plotted FTL course, through open space", true);
            Swatch(ref y, x, w, FrontColor, "war front (dashed) or shut gate (bar)", true);
            Swatch(ref y, x, w, PirateColor, "raiders about", false);
            y += 6;

            Heading(ref y, x, w, "WARS KNOWN");
            if (model.Wars.Count == 0) Line(ref y, x, w, "none: the polities are at peace", Dim);
            foreach (var war in model.Wars)
            {
                if (y > r.yMax - 110) break;
                Line(ref y, x, w, war.A.Name + "  vs  " + war.B.Name, FrontColor);
            }
            y += 6;

            Heading(ref y, x, w, "HOLDERS");
            var seen = new List<Faction>();
            foreach (var m in model.Systems)
            {
                if (m.Holder == null || seen.Contains(m.Holder)) continue;
                seen.Add(m.Holder);
                if (y > r.yMax - 90) break;
                int held = 0;
                foreach (var n in model.Systems)
                    if (n.Holder == m.Holder) held++;
                Line(ref y, x, w, "■  " + m.Holder.Name + "   " + held + (held == 1 ? " system" : " systems"), Tint(m.Holder.Color, 1));
            }

            var old = GUI.contentColor;
            GUI.contentColor = Dim;
            GUI.Label(new Rect(x, r.yMax - 92, w, 86),
                "Click a star to inspect it, double-click to travel.\nAt the helm: plot an FTL course to any star.\nDrag to turn, shift-drag to pan, wheel to zoom.\n[ or Esc closes the map.", small);
            GUI.contentColor = old;
        }

        void DrawDetail(Rect r, double jd)
        {
            GUI.Box(r, GUIContent.none, Panel);
            if (selectedStar is NearbyStar star)
            {
                DrawStarDetail(r, star);
                return;
            }
            var s = selected ?? app.ActiveSystem;
            var m = model.Of(s);
            if (m == null) return;
            float x = r.x + 14, w = r.width - 28, y = r.y + 10;
            GUI.Label(new Rect(x, y, w, 22), s.Name.ToUpperInvariant(), title);
            y += 24;
            bool here = s == app.ActiveSystem;
            if (here) Line(ref y, x, w, "You are here: seen as it is.", Accent);
            else
            {
                var route = app.Galaxy.Network.Route(app.ActiveSystem, s, null, jd);
                string way = route == null ? "no way through the gates" : route.Count + (route.Count == 1 ? " gate, " : " gates, ") +
                                                                     Days(GateNetwork.RouteDays(route)) + " in the gates";
                Line(ref y, x, w, Galaxy.DistanceLy(app.ActiveSystem, s).ToString("0.00", CultureInfo.InvariantCulture) + " ly away   ·   " + way, Color.white);
                var openRoute = app.Galaxy.Network.Route(app.ActiveSystem, s, app.Piloted.Ship?.Faction ?? app.ActiveSystem.Holder, jd);
                if (route != null && openRoute == null) Line(ref y, x, w, "Every way there is shut to you just now", FrontColor);
                Line(ref y, x, w, app.Galaxy.Traffic?.News.Describe(app.ActiveSystem, s, jd) ?? "no news", Dim);
                if (GUI.Button(new Rect(x, y + 2, w, 28), app.IsPiloting ? "PLOT A COURSE HERE THROUGH THE GATES" : "TRAVEL HERE THROUGH THE GATES", buttonOn)) Travel(s);
                y += 36;
                DrawFtlSection(ref y, x, w, FtlTarget.OfStar(s));
            }
            y += 4;

            if (!m.Known)
            {
                Line(ref y, x, w, "Nothing has ever been heard from it: a star on the charts, and its gates.", Dim);
                y += 6;
            }
            else
            {
                Heading(ref y, x, w, "WHO HOLDS IT");
                if (m.Holdings.Count == 0) Line(ref y, x, w, "Nobody: no settled markets", Dim);
                foreach (var h in m.Holdings)
                    Line(ref y, x, w, h.Faction.Name + (h.Faction == m.Holder ? "  (holds it)" : "") + "   " + h.Ports + (h.Ports == 1 ? " port, " : " ports, ") + People(h.Population), Tint(h.Faction.Color, 1));
                if (m.Contested) Line(ref y, x, w, "Contested: " + m.ContestedBy.A.Name + " and " + m.ContestedBy.B.Name + " are at war here", FrontColor);
                foreach (var t in m.Threats)
                {
                    string what = t.BesiegedBy != null ? "under siege by the " + t.BesiegedBy.Name : "blockaded by the " + t.BlockadedBy.Name;
                    Line(ref y, x, w, t.Port + " " + what, FrontColor);
                }
                y += 6;

                Heading(ref y, x, w, "RAIDERS");
                if (m.Pirates == PirateActivity.None) Line(ref y, x, w, "None reported", Dim);
                else
                    Line(ref y, x, w, (m.Pirates == PirateActivity.High ? "Heavy: " : "Some: ") + m.PirateShips + " armed raiders, " + m.Havens +
                                      (m.Havens == 1 ? " haven known, " : " havens known, ") + m.Raids + (m.Raids == 1 ? " raid" : " raids") + " through its gates", PirateColor);
                y += 6;
            }

            Heading(ref y, x, w, "GATES");
            foreach (var l in model.Links)
            {
                if (l.A != s && l.B != s) continue;
                if (y > r.yMax - 24) break;
                var other = l.Other(s);
                string line;
                Color c;
                if (l.UnderConstruction)
                {
                    line = "to " + other.Name + ": being built, " + (l.Progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%" + (l.Builder != null ? " by the " + l.Builder.Name : "");
                    c = BuildColor;
                }
                else
                {
                    line = "to " + other.Name + "   " + l.Link.Ly.ToString("0.00", CultureInfo.InvariantCulture) + " ly";
                    if (!l.Known) line += "   traffic unknown";
                    else
                    {
                        line += "   " + l.Crossings + (l.Crossings == 1 ? " crossing" : " crossings");
                        if (l.Tonnes > 0) line += ", " + Tonnes(l.Tonnes);
                        if (l.Raids > 0) line += ", " + l.Raids + (l.Raids == 1 ? " raid" : " raids");
                    }
                    bool shutHere = l.A == s ? l.ShutAtA : l.ShutAtB;
                    bool shutThere = l.A == s ? l.ShutAtB : l.ShutAtA;
                    if (shutHere) line += "   SHUT here";
                    else if (shutThere) line += "   SHUT at the far end";
                    if (l.WarFront) line += "   FRONT";
                    c = l.Shut || l.WarFront ? FrontColor : l.Known ? Color.white : Dim;
                }
                Line(ref y, x, w, line, c);
            }
        }

        // ---------------------------------------------------------------- command (U.SPC-T423)

        /// <summary>The states that can be commanded: every polity with a navy and a port somewhere.</summary>
        List<Faction> CommandableStates()
        {
            var polities = app.Galaxy.Polities;
            var list = new List<Faction>();
            foreach (var f in polities.Factions)
            {
                if (f.IsPirate || !f.IsMilitary) continue;
                foreach (var _ in polities.SettlementsOf(f))
                {
                    list.Add(f);
                    break;
                }
            }
            return list;
        }

        /// <summary>
        /// The strategy layer (U.SPC-T423): one polity at a glance (its systems, people, money, navy and wars), its production,
        /// build and research priorities, every one of its fleet groups with what it is doing and what it was told to do, and
        /// objectives (take, hold, blockade, escort, explore) for the system picked on the map, given to one squadron, every
        /// group in a system, or the whole navy.
        /// </summary>
        void DrawCommand(Rect r, double jd)
        {
            var command = app.Galaxy.Polities.Command;
            var states = CommandableStates();
            float x = r.x + 14, w = r.width - 28, y = r.y + 10;
            if (states.Count == 0)
            {
                Line(ref y, x, w, "No polity with a navy to command.", Dim);
                return;
            }
            if (commanded == null || !states.Contains(commanded))
            {
                var mine = command.Player ?? app.Piloted.Ship?.Faction;
                var holder = app.ActiveSystem.Holder;
                commanded = mine != null && states.Contains(mine) ? mine : holder != null && states.Contains(holder) ? holder : states[0];
                if (!CoopSpeaks(commanded)) // US-T47: the party's speaker goes through the frames
                command.Player = commanded;
                reportAt = -10f;
            }
            if (report == null || report.Faction != commanded || Time.unscaledTime - reportAt > 0.5f)
            {
                report = command.Report(commanded, jd);
                reportAt = Time.unscaledTime;
            }
            var inv = CultureInfo.InvariantCulture;

            if (GUI.Button(new Rect(x, y, 26, 22), "<", button)) Cycle(states, -1);
            if (GUI.Button(new Rect(x + w - 26, y, 26, 22), ">", button)) Cycle(states, 1);
            var old = GUI.contentColor;
            GUI.contentColor = Tint(commanded.Color, 1);
            GUI.Label(new Rect(x + 34, y + 2, w - 68, 22), commanded.Name.ToUpperInvariant(), title);
            GUI.contentColor = old;
            y += 28;

            Line(ref y, x, w, report.Systems + (report.Systems == 1 ? " system held, " : " systems held, ") + report.Ports + (report.Ports == 1 ? " port, " : " ports, ") +
                              People(report.Population), Color.white);
            Line(ref y, x, w, "Treasury " + report.Treasury.ToString("N0", inv) + " cr   ·   unrest " + (report.Unrest * 100).ToString("0", inv) + "%", Color.white);
            Line(ref y, x, w, "Navy: " + report.Warships + " hulls in " + report.Groups + " groups (" + report.GateFleets + " gate, " + report.StrikeGroups + " jump strike), strength " + report.Strength.ToString("N0", inv) + "; " +
                              report.Commanded + " under your orders" + (report.Campaigns > 0 ? ", " + report.Campaigns + " on campaign" : ""), Color.white);
            Line(ref y, x, w, report.AtWarWith.Count == 0 ? "At peace" : "At war with the " + string.Join(", the ", report.AtWarWith.ConvertAll(f => f.Name)),
                report.AtWarWith.Count == 0 ? Dim : FrontColor);
            Line(ref y, x, w, "Marines x" + report.MarineQuality.ToString("0.00", inv) + ", crew morale " + report.Morale.ToString("0.00", inv) + ", " +
                              report.CrewLost.ToString("N0", inv) + " spacers lost", Dim);
            y += 4;

            Heading(ref y, x, w, "PRIORITIES");
            var d = CoopDirectives(command, commanded); // US-T47: command.Of(commanded) in single-player
            if (GUI.Button(new Rect(x, y, w, 22), "Production: " + d.Production.Name, button))
                if (!CoopSendsProduction(Next(ProductionFocus.All, d.Production))) // US-T47
                command.SetProduction(commanded, Next(ProductionFocus.All, d.Production));
            y += 24;
            Line(ref y, x, w, d.Production.Description, Dim);
            if (GUI.Button(new Rect(x, y, w, 22), "Build: " + d.Build.Name, button)) { if (!CoopSendsBuild(Next(BuildPlan.All, d.Build))) command.SetBuild(commanded, Next(BuildPlan.All, d.Build)); } // US-T47
            y += 24;
            Line(ref y, x, w, d.Build.Description, Dim);
            string research = d.Research == null ? "nothing"
                : d.Research.Name + " " + (d.Level(d.Research) + 1) + "  (" + d.ResearchPoints.ToString("0", inv) + " of " + d.Research.CostOf(d.Level(d.Research)).ToString("0", inv) + ")";
            if (d.Research != null && d.Level(d.Research) >= d.Research.MaxLevel) research = d.Research.Name + ": every level reached";
            if (GUI.Button(new Rect(x, y, w, 22), "Research: " + research, button))
            {
                var tracks = new List<ResearchTrack> { null };
                foreach (var t in ResearchTrack.All) tracks.Add(t);
                if (!CoopSendsResearch(tracks[(tracks.IndexOf(d.Research) + 1) % tracks.Count])) // US-T47
                command.SetResearch(commanded, tracks[(tracks.IndexOf(d.Research) + 1) % tracks.Count]);
            }
            y += 24;
            var levels = new List<string>();
            foreach (var t in ResearchTrack.All) levels.Add(t.Name + " " + d.Level(t));
            Line(ref y, x, w, string.Join("   ·   ", levels), Dim);
            y += 4;

            var target = selected ?? app.ActiveSystem;
            Heading(ref y, x, w, "ORDERS FOR " + target.Name.ToUpperInvariant());
            float third = (w - 8) / 3;
            string[] scopes = { "SQUADRON", "SYSTEM", "EMPIRE" };
            for (int i = 0; i < 3; i++)
                if (GUI.Button(new Rect(x + i * (third + 4), y, third, 22), scopes[i], (int)commandScope == i ? buttonOn : button)) commandScope = (CommandScope)i;
            y += 26;
            var from = commandGroup != null ? command.WorldOf(commandGroup) : null;
            string who = commandScope == CommandScope.Group ? (commandGroup != null ? "the " + commandGroup.Name + "'s group" : "pick a group below")
                : commandScope == CommandScope.System ? "every group in " + (command.SystemOf(from ?? app.ActiveSystem.World)?.Name ?? "this system")
                : "every group of the " + commanded.Name;
            Line(ref y, x, w, "To: " + who, Dim);
            ObjectiveKind[] kinds = { ObjectiveKind.Take, ObjectiveKind.Hold, ObjectiveKind.Blockade, ObjectiveKind.Escort, ObjectiveKind.Explore };
            float fifth = (w - 16) / 5;
            for (int i = 0; i < kinds.Length; i++)
                if (GUI.Button(new Rect(x + i * (fifth + 4), y, fifth, 24), kinds[i].ToString().ToUpperInvariant(), buttonOn)) Give(command, kinds[i], target, from, jd);
            y += 28;
            if (GUI.Button(new Rect(x, y, w, 22), "CLEAR ORDERS (back to their own command's plans)", button))
            {
                if (CoopSendsClear(from ?? app.ActiveSystem.World)) { reportAt = -10f; return; } // US-T47
                int n = command.Clear(commanded, commandScope, commandGroup, from ?? app.ActiveSystem.World);
                app.ShowToast(n == 0 ? "No group there was under orders" : n + (n == 1 ? " group stands down" : " groups stand down"));
                reportAt = -10f;
            }
            y += 28;

            Heading(ref y, x, w, "FLEET  (" + report.Fleet.Count + " groups)");
            var list = new Rect(x - 4, y, w + 8, Mathf.Max(40, r.yMax - y - 8));
            const float rowH = 34;
            groupScroll = GUI.BeginScrollView(list, groupScroll, new Rect(0, 0, w - 12, report.Fleet.Count * rowH));
            for (int i = 0; i < report.Fleet.Count; i++)
            {
                var g = report.Fleet[i];
                bool on = g.Leader == commandGroup;
                if (GUI.Button(new Rect(0, i * rowH, w - 12, rowH - 2), GUIContent.none, on ? buttonOn : button))
                {
                    commandGroup = g.Leader;
                    commandScope = CommandScope.Group;
                }
                GUI.Label(new Rect(6, i * rowH + 1, w - 24, 16),
                    g.Leader.Name + "  (" + g.Hulls + (g.Hulls == 1 ? " hull" : " hulls") + ", " + g.Strength.ToString("N0", inv) + ")   " + g.System, labelDim);
                var c = GUI.contentColor;
                GUI.contentColor = string.IsNullOrEmpty(g.Objective) ? Dim : Accent;
                GUI.Label(new Rect(6, i * rowH + 16, w - 24, 16), string.IsNullOrEmpty(g.Objective) ? g.Order : g.Objective + "  ·  " + g.Order, small);
                GUI.contentColor = c;
            }
            GUI.EndScrollView();
        }

        void Cycle(List<Faction> states, int step)
        {
            int i = states.IndexOf(commanded);
            commanded = states[((i < 0 ? 0 : i) + step + states.Count) % states.Count];
            if (!CoopSpeaks(commanded)) // US-T47
            app.Galaxy.Polities.Command.Player = commanded;
            commandGroup = null;
            reportAt = -10f;
        }

        static T Next<T>(IReadOnlyList<T> all, T current) where T : class
        {
            for (int i = 0; i < all.Count; i++)
                if (all[i] == current) return all[(i + 1) % all.Count];
            return all.Count > 0 ? all[0] : null;
        }

        void Give(StrategicCommand command, ObjectiveKind kind, StarSystem target, World from, double jd)
        {
            if (commandScope == CommandScope.Group && commandGroup == null)
            {
                app.ShowToast("Pick a group from the fleet list first, or give the order to a whole system or the empire");
                return;
            }
            // An escort for your own hull, when you are flying in that system and are no enemy of theirs; else command's pick.
            Ship charge = null;
            var pilot = app.Piloted.Ship;
            if (kind == ObjectiveKind.Escort && pilot != null && target.World.Fleet.IsPresent(pilot) && !app.Galaxy.Polities.Diplomacy.Hostile(commanded, pilot.Faction))
                charge = pilot;
            if (CoopSendsOrder(kind, target, from, charge)) { reportAt = -10f; return; } // US-T47
            string why = command.Order(commanded, commandScope, commandGroup, from ?? app.ActiveSystem.World, kind, target.World, jd, out int given, -1, charge);
            app.ShowToast(why ?? given + (given == 1 ? " group: " : " groups: ") + command.Describe(kind, target.World, -1, charge).ToLowerInvariant());
            reportAt = -10f;
        }

        // ---------------------------------------------------------------- diplomacy (U.SPC-T427)

        /// <summary>
        /// The envoy screen (U.SPC-T427): the state you speak for (the one commanded), the proposals other states have put to
        /// it waiting on your answer, every other state with its mood toward you and how you stand, and for the one picked
        /// its answer to your hail, the pacts between you (each can be broken), every deal your envoy can put with how they
        /// would take it (the same weighing the AI answers by), a gift, and a declaration of war (clicked twice).
        /// </summary>
        void DrawDiplomacy(Rect r, double jd)
        {
            var statecraft = app.Galaxy.Polities.Statecraft;
            var command = app.Galaxy.Polities.Command;
            var states = CommandableStates().FindAll(Statecraft.Party);
            float x = r.x + 14, w = r.width - 28, top = r.y + 10;
            if (states.Count == 0)
            {
                Line(ref top, x, w, "No state to speak for.", Dim);
                return;
            }
            if (commanded == null || !states.Contains(commanded))
            {
                var mine = command.Player ?? app.Piloted.Ship?.Faction;
                var holder = app.ActiveSystem.Holder;
                commanded = mine != null && states.Contains(mine) ? mine : holder != null && states.Contains(holder) ? holder : states[0];
                envoyWith = null;
            }
            CoopAdoptSpeaker(states); // US-T47: the party's speaker, which another player may have picked
            if (!CoopSpeaks(commanded)) // US-T47
            command.Player = commanded;
            var inv = CultureInfo.InvariantCulture;

            if (GUI.Button(new Rect(x, top, 26, 22), "<", button)) CycleSpeaker(states, -1);
            if (GUI.Button(new Rect(x + w - 26, top, 26, 22), ">", button)) CycleSpeaker(states, 1);
            var old = GUI.contentColor;
            GUI.contentColor = Tint(commanded.Color, 1);
            GUI.Label(new Rect(x + 34, top + 2, w - 68, 22), commanded.Name.ToUpperInvariant(), title);
            GUI.contentColor = old;
            top += 28;

            var view = new Rect(r.x + 4, top, r.width - 8, r.yMax - top - 6);
            diplomacyScroll = GUI.BeginScrollView(view, diplomacyScroll, new Rect(0, 0, view.width - 16, Mathf.Max(diplomacyHeight, view.height)));
            x = 10;
            w = view.width - 36;
            float y = 0;
            Line(ref y, x, w, "Treasury " + commanded.Treasury.ToString("N0", inv) + " cr" + (statecraft.OverlordOf(commanded) != null ? "   ·   vassal of the " + statecraft.OverlordOf(commanded).Name : ""), Color.white);

            // What the others have put to you.
            var waiting = new List<Proposal>();
            foreach (var p in statecraft.Inbox)
                if (p.To == commanded) waiting.Add(p);
            if (waiting.Count > 0)
            {
                Heading(ref y, x, w, "AWAITING YOUR ANSWER");
                foreach (var p in waiting)
                {
                    double left = Statecraft.InboxDays - (jd - p.Jd);
                    Line(ref y, x, w, Capital(p.Describe()) + "  (" + Math.Max(0, left).ToString("0", inv) + " days to answer)", Accent);
                    float half = (w - 4) / 2;
                    if (GUI.Button(new Rect(x, y, half, 22), "ACCEPT", buttonOn))
                        if (!CoopSendsAnswer(p, true)) // US-T47
                        app.ShowToast(statecraft.Answer(p, true, jd) ? "Agreed: " + p.Describe() : "That offer no longer stands");
                    if (GUI.Button(new Rect(x + half + 4, y, half, 22), "DECLINE", button))
                    {
                        if (CoopSendsAnswer(p, false)) { y += 26; continue; } // US-T47
                        statecraft.Answer(p, false, jd);
                        app.ShowToast("Declined: " + p.Describe());
                    }
                    y += 26;
                }
            }

            Heading(ref y, x, w, "TREAT WITH");
            foreach (var g in statecraft.Parties())
            {
                if (g == commanded) continue;
                if (envoyWith == null) envoyWith = g;
                bool on = g == envoyWith;
                if (GUI.Button(new Rect(x, y, w, 34), GUIContent.none, on ? buttonOn : button))
                {
                    envoyWith = g;
                    warArmed = false;
                }
                var c = GUI.contentColor;
                GUI.contentColor = Tint(g.Color, 1);
                GUI.Label(new Rect(x + 6, y + 1, w - 12, 16), g.Name, labelDim);
                GUI.contentColor = statecraft.Polities.Diplomacy.Get(commanded, g) == Relation.War ? FrontColor : Dim;
                GUI.Label(new Rect(x + 6, y + 16, w - 12, 16), Statecraft.Mood(statecraft.Opinion(g, commanded)) + "  ·  " + statecraft.Standing(commanded, g), small);
                GUI.contentColor = c;
                y += 36;
            }

            if (envoyWith != null && envoyWith != commanded && Statecraft.Party(envoyWith))
            {
                var them = envoyWith;
                y += 4;
                Heading(ref y, x, w, "ENVOY TO THE " + them.Name.ToUpperInvariant());
                Line(ref y, x, w, statecraft.Hail(commanded, them, jd), Color.white);
                Line(ref y, x, w, "They think of you " + statecraft.Opinion(them, commanded).ToString("0", inv) + " (" + Statecraft.Mood(statecraft.Opinion(them, commanded)) +
                                  "); you of them " + statecraft.Opinion(commanded, them).ToString("0", inv) + ".  Navies " +
                                  statecraft.Power(commanded).ToString("N0", inv) + " to " + statecraft.Power(them).ToString("N0", inv), Dim);

                foreach (var p in statecraft.PactsOf(commanded))
                {
                    if (!p.Involves(them)) continue;
                    string until = double.IsNaN(p.UntilJd) ? "" : "  (" + Math.Max(0, p.UntilJd - jd).ToString("0", inv) + " days left)";
                    Line(ref y, x, w, Capital(p.Describe()) + until, CourseColor);
                    if (GUI.Button(new Rect(x, y, w, 20), p.Kind == PactKind.Vassalage && p.A == commanded ? "THROW OFF THEIR RULE" : "BREAK IT", button))
                    {
                        if (CoopSendsBreak(p)) { y += 24; continue; } // US-T47
                        statecraft.Break(p, commanded, jd);
                        app.ShowToast("You break the " + p.Describe() + ": they will not forget it");
                    }
                    y += 24;
                }

                Heading(ref y, x, w, "PROPOSE");
                foreach (var o in statecraft.Envoy(commanded, them, jd))
                {
                    string name = o.Deal.Name.ToUpperInvariant() + (o.Deal.HasAmount ? "  " + o.Amount.ToString("N0", inv) + " cr a month" : "");
                    bool can = o.Unavailable == null;
                    var was = GUI.enabled;
                    GUI.enabled = was && can;
                    if (GUI.Button(new Rect(x, y, w, 22), name, can && o.Read.Accepted ? buttonOn : button))
                    {
                        if (!CoopSendsPropose(o, them)) { // US-T47
                        string why = statecraft.Propose(o.Deal, commanded, them, jd, out var answer, o.Amount);
                        app.ShowToast(why ?? (answer == null ? "Put to them: they will answer" : (answer.Accepted ? "Agreed: " : "Refused: ") + answer.Main));
                        } // US-T47
                    }
                    GUI.enabled = was;
                    y += 24;
                    if (!can) Line(ref y, x + 8, w - 8, o.Unavailable, Unknown);
                    else Line(ref y, x + 8, w - 8, (o.Read.Accepted ? "They would agree: " : "They would refuse: ") + o.Read.Main, o.Read.Accepted ? CourseColor : FrontColor);
                }

                double gift = Statecraft.SuggestedGift(commanded);
                if (GUI.Button(new Rect(x, y, w, 22), "SEND A GIFT  " + gift.ToString("N0", inv) + " cr", button))
                    if (!CoopSendsGift(them, gift)) // US-T47
                    app.ShowToast(statecraft.Gift(commanded, them, gift, jd) ?? "The " + them.Name + " receives your gift");
                y += 26;
                if (statecraft.Polities.Diplomacy.Get(commanded, them) != Relation.War)
                {
                    if (GUI.Button(new Rect(x, y, w, 24), warArmed ? "CONFIRM: DECLARE WAR (breaks every pact with them)" : "DECLARE WAR", buttonOn))
                    {
                        if (!warArmed) warArmed = true;
                        else
                        {
                            warArmed = false;
                            if (!CoopSendsWar(them)) { // US-T47
                            string why = statecraft.DeclareWar(commanded, them, jd);
                            app.ShowToast(why ?? "The " + commanded.Name + " is at war with the " + them.Name);
                            } // US-T47
                        }
                    }
                    y += 28;
                }
            }

            Heading(ref y, x, w, "LATELY");
            var record = statecraft.Record;
            if (record.Count == 0) Line(ref y, x, w, "Nothing has passed between the states yet.", Dim);
            for (int i = record.Count - 1; i >= 0 && i >= record.Count - 8; i--) Line(ref y, x, w, record[i], Dim);
            if (Event.current.type == EventType.Repaint) diplomacyHeight = y + 10;
            GUI.EndScrollView();
        }

        void CycleSpeaker(List<Faction> states, int step)
        {
            int i = states.IndexOf(commanded);
            commanded = states[((i < 0 ? 0 : i) + step + states.Count) % states.Count];
            if (!CoopSpeaks(commanded)) // US-T47
            app.Galaxy.Polities.Command.Player = commanded;
            envoyWith = null;
            warArmed = false;
            commandGroup = null;
            reportAt = -10f;
        }

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>A plain star on the charts (U.SPC-T420): what is known of it, and an FTL course to it.</summary>
        void DrawStarDetail(Rect r, NearbyStar star)
        {
            float x = r.x + 14, w = r.width - 28, y = r.y + 10;
            GUI.Label(new Rect(x, y, w, 22), star.Name.ToUpperInvariant(), title);
            y += 24;
            var inv = CultureInfo.InvariantCulture;
            Line(ref y, x, w, (star.PositionLy - app.ActiveSystem.PositionLy).Length.ToString("0.00", inv) + " ly away   ·   " +
                              (string.IsNullOrEmpty(star.SpectralType) ? "type unknown" : star.SpectralType + " star"), Color.white);
            Line(ref y, x, w, "A star on the charts: nobody simulates it yet and no gate goes there. A hull with its own FTL drive can fly " +
                              "to it, and drops out at the edge of its well.", Dim);
            y += 6;
            DrawFtlSection(ref y, x, w, FtlTarget.OfStar(star));
        }

        /// <summary>
        /// FTL to <paramref name="target"/> (U.SPC-T420): at the helm of a hull with its own drive, PLOT FTL COURSE works the
        /// course out (shown here once plotted: the run out, the jump, any wells on the way, the ETA and the fuel) and ENGAGE
        /// AUTOPILOT flies it.
        /// </summary>
        void DrawFtlSection(ref float y, float x, float w, FtlTarget target)
        {
            Heading(ref y, x, w, "FTL");
            if (!app.IsPiloting)
            {
                Line(ref y, x, w, "Take the helm of a capital ship or an explorer (P) to plot an FTL course here: no gates needed.", Dim);
                y += 4;
                return;
            }
            if (!app.CanPlotFtl)
            {
                Line(ref y, x, w, app.RidingLane ? "Not from a lane: come off it first." : HelmFtl.Refusal(app.Piloted.Ship), Dim);
                y += 4;
                return;
            }
            var course = app.PlottedCourse;
            bool plotted = course != null && FtlNavigation.Same(course.Target, target);
            if (plotted)
            {
                var inv = CultureInfo.InvariantCulture;
                if (!course.Flyable) Line(ref y, x, w, course.Refusal, FrontColor);
                else
                {
                    if (course.NeedsOutRun)
                        Line(ref y, x, w, "Out to the rim: " + (course.OutRunKm / Astro.AuKm).ToString("0.##", inv) + " AU under the ordinary drive, " +
                                          FtlNavigation.Describe(course.OutRunDays), Color.white);
                    if (course.CoolDays > 0) Line(ref y, x, w, "The drive cools first: " + FtlNavigation.Describe(course.CoolDays), Color.white);
                    Line(ref y, x, w, "Jump: " + course.Ly.ToString("0.00", inv) + " ly, charging " + FtlNavigation.Describe(course.SpoolDays) + " and " +
                                      FtlNavigation.Describe(course.FtlDays) + " under FTL", Color.white);
                    if (course.Interdictions.Count > 0)
                        Line(ref y, x, w, "Pulled out by " + string.Join(", ", course.Interdictions) + " on the way: " +
                                          FtlNavigation.Describe(course.CrossingDays) + " crossing under the ordinary drive", PirateColor);
                    Line(ref y, x, w, "ETA " + Astro.ToDateTime(course.EtaJd).ToString("yyyy-MM-dd HH:mm", inv) + " UTC, " + FtlNavigation.Describe(course.TotalDays) +
                                      " from now", CourseColor);
                    Line(ref y, x, w, "Reaction mass: " + course.FuelTonnes.ToString("N0", inv) + " t of " + course.TanksTonnes.ToString("N0", inv) +
                                      " t in the tanks (one jump reaches " + (double.IsInfinity(course.RangeLy) ? "anywhere" : course.RangeLy.ToString("0.#", inv) + " ly") + ")", Dim);
                }
            }
            if (GUI.Button(new Rect(x, y + 2, w * 0.48f, 28), plotted ? "PLOT AGAIN" : "PLOT FTL COURSE", button)) app.PlotFtlCourse(target);
            if (GUI.Button(new Rect(x + w * 0.52f, y + 2, w * 0.48f, 28), "ENGAGE AUTOPILOT", buttonOn))
            {
                if (app.PlotFtlCourse(target, quiet: true) && app.EngageFtlAutopilot()) app.ShowGalaxyMap = false;
                else if (app.PlottedCourse != null && !app.PlottedCourse.Flyable) app.ShowToast(UnitySpaceApp.CourseSummary(app.PlottedCourse));
            }
            y += 36;
        }

        static string Days(double d) => d < 1 ? (d * 24).ToString("0", CultureInfo.InvariantCulture) + " h" : d.ToString("0.0", CultureInfo.InvariantCulture) + " days";

        static string People(double n) => n >= 1e9 ? (n / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " bn people"
            : n >= 1e6 ? (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " m people"
            : n.ToString("N0", CultureInfo.InvariantCulture) + " people";

        static string Tonnes(double t) => t >= 1e6 ? (t / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " Mt"
            : t >= 1e3 ? (t / 1e3).ToString("0", CultureInfo.InvariantCulture) + " kt"
            : t.ToString("0", CultureInfo.InvariantCulture) + " t";

        // ---------------------------------------------------------------- drawing helpers

        GUIStyle Panel => panelStyle ??= new GUIStyle { normal = { background = panelTex }, border = new RectOffset(2, 2, 2, 2) };
        GUIStyle panelStyle;

        void Heading(ref float y, float x, float w, string s)
        {
            var old = GUI.contentColor;
            GUI.contentColor = Accent;
            GUI.Label(new Rect(x, y, w, 16), s, small);
            GUI.contentColor = old;
            y += 18;
        }

        void Line(ref float y, float x, float w, string s, Color c)
        {
            var content = new GUIContent(s);
            float h = Mathf.Max(16, text.CalcHeight(content, w));
            var old = GUI.contentColor;
            GUI.contentColor = c;
            GUI.Label(new Rect(x, y, w, h), content, text);
            GUI.contentColor = old;
            y += h + 2;
        }

        void Toggle(ref float y, float x, float w, ref bool on, string name, string hint)
        {
            if (GUI.Button(new Rect(x, y, w, 22), (on ? "[x]  " : "[  ]  ") + name, on ? buttonOn : button)) on = !on;
            y += 24;
            var old = GUI.contentColor;
            GUI.contentColor = Dim;
            GUI.Label(new Rect(x + 4, y, w - 4, 14), hint, small);
            GUI.contentColor = old;
            y += 17;
        }

        void Swatch(ref float y, float x, float w, Color c, string s, bool dashed)
        {
            if (Event.current.type == EventType.Repaint)
            {
                var a = new Vector2(x, y + 8);
                var b = new Vector2(x + 30, y + 8);
                if (dashed) Dashed(a, b, c, 2.5f, 6, 4);
                else Line(a, b, c, 2.5f);
            }
            var old = GUI.contentColor;
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(x + 38, y, w - 38, 16), s, small);
            GUI.contentColor = old;
            y += 18;
        }

        void Label(Vector2 p, string s, Color c, bool minor)
        {
            var style = minor ? labelDim : label;
            var content = new GUIContent(s);
            var size = style.CalcSize(content);
            var old = GUI.contentColor;
            GUI.contentColor = new Color(0, 0, 0, 0.85f);
            GUI.Label(new Rect(p.x + 1, p.y + 1, size.x, size.y), content, style);
            GUI.contentColor = c;
            GUI.Label(new Rect(p.x, p.y, size.x, size.y), content, style);
            GUI.contentColor = old;
        }

        static void Line(Vector2 a, Vector2 b, Color color, float thickness)
        {
            var d = b - a;
            float length = d.magnitude;
            if (length < 0.5f) return;
            var matrix = GUI.matrix;
            var old = GUI.color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.color = color;
            GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, length, thickness), Icons.White);
            GUI.color = old;
            GUI.matrix = matrix;
        }

        static void Dashed(Vector2 a, Vector2 b, Color color, float thickness, float dash, float gap)
        {
            float length = (b - a).magnitude;
            if (length < 0.5f) return;
            var d = (b - a) / length;
            for (float s = 0; s < length; s += dash + gap)
                Line(a + d * s, a + d * Mathf.Min(s + dash, length), color, thickness);
        }

        void Disc(Vector2 p, float r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - r, p.y - r, r * 2, r * 2), glow);
            GUI.color = old;
        }

        static void Ring(Vector2 p, float r, Color c, float thickness)
        {
            const int n = 28;
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2 / n, a1 = (i + 1) * Mathf.PI * 2 / n;
                Line(p + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, p + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, c, thickness);
            }
        }

        static void Brackets(Vector2 p, float half, Color c)
        {
            float len = half * 0.5f;
            var old = GUI.color;
            GUI.color = c;
            var w = Icons.White;
            var r = new Rect(p.x - half, p.y - half, half * 2, half * 2);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, len, 2), w);
            GUI.DrawTexture(new Rect(r.xMin, r.yMin, 2, len), w);
            GUI.DrawTexture(new Rect(r.xMax - len, r.yMin, len, 2), w);
            GUI.DrawTexture(new Rect(r.xMax - 2, r.yMin, 2, len), w);
            GUI.DrawTexture(new Rect(r.xMin, r.yMax - 2, len, 2), w);
            GUI.DrawTexture(new Rect(r.xMin, r.yMax - len, 2, len), w);
            GUI.DrawTexture(new Rect(r.xMax - len, r.yMax - 2, len, 2), w);
            GUI.DrawTexture(new Rect(r.xMax - 2, r.yMax - len, 2, len), w);
            GUI.color = old;
        }

        void EnsureStyles()
        {
            if (title != null) return;
            panelTex = Hud.Solid(new Color(0.03f, 0.05f, 0.08f, 0.86f), new Color(0.3f, 0.6f, 0.75f, 0.45f));
            buttonTex = Hud.Solid(new Color(0.12f, 0.18f, 0.24f, 0.95f), new Color(0.3f, 0.5f, 0.6f, 0.6f));
            buttonOnTex = Hud.Solid(new Color(0.1f, 0.3f, 0.42f, 0.95f), new Color(0.45f, 0.85f, 1f, 0.8f));
            glow = SoftDisc();
            title = new GUIStyle { fontSize = 15, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            text = new GUIStyle { fontSize = 12, normal = { textColor = Color.white }, wordWrap = true };
            small = new GUIStyle { fontSize = 11, normal = { textColor = Dim }, wordWrap = true };
            label = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            labelDim = new GUIStyle { fontSize = 11, normal = { textColor = Color.white } };
            button = new GUIStyle
            {
                fontSize = 12, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 6, 0, 0), border = new RectOffset(2, 2, 2, 2),
                normal = { background = buttonTex, textColor = Color.white },
                hover = { background = buttonOnTex, textColor = Color.white },
                active = { background = buttonOnTex, textColor = Accent },
            };
            buttonOn = new GUIStyle(button) { normal = { background = buttonOnTex, textColor = Color.white }, fontStyle = FontStyle.Bold };
        }

        /// <summary>A white disc fading out to its edge, tinted at draw time.</summary>
        static Texture2D SoftDisc()
        {
            const int n = 64;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1 - r);
                    t.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(a * a * 1.6f + (r < 0.5f ? 0.35f * (1 - r * 2) : 0))));
                }
            t.Apply();
            return t;
        }

        /// <summary>The map's own styles are going with the HUD that owns it (U.SPC-T366): DontSave outlives the scene
        /// otherwise, so every game session that opened the map would leave its textures behind.</summary>
        public void DestroyOwnedTextures()
        {
            if (panelTex != null) UnityEngine.Object.Destroy(panelTex);
            if (buttonTex != null) UnityEngine.Object.Destroy(buttonTex);
            if (buttonOnTex != null) UnityEngine.Object.Destroy(buttonOnTex);
            if (glow != null) UnityEngine.Object.Destroy(glow);
            panelTex = buttonTex = buttonOnTex = glow = null;
        }
    }
}
