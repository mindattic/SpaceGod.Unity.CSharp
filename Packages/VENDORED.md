# Vendored packages

SpaceGod.Unity.CSharp is self-contained: these two MindAttic Unity packages are embedded here (Unity loads any
package folder under `Packages/` as an embedded package, so `manifest.json` no longer names them). They used to
be loaded by path from repos cloned next to this one. The copies are the git-tracked files of each package
folder at the commit below, unchanged. Re-vendor by copying the folder again and updating the hash here.

| Package | Source repo | Source folder | Commit |
|---|---|---|---|
| `com.mindattic.capture` | [mindattic/MindAttic.Capture](https://github.com/mindattic/MindAttic.Capture) | `unity/com.mindattic.capture` | `c4272836318e00dcdbb2965faafa0b03da3c5e66` |
| `com.mindattic.unity.shared` | [mindattic/Unity.Shared](https://github.com/mindattic/Unity.Shared) | `unity/com.mindattic.unity.shared` | `3ce3b8a4bd965833c34ee9c9d0db1eb61dab0fc6` |

`Tests/Unity.Shared.Core` and `Bench/CSharp/Lib/Unity.Shared.Core` compile the embedded
`com.mindattic.unity.shared/Runtime/Core` for the engine-free tests and the Bench.
