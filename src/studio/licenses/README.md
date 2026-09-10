# Studio attribution and notices

This directory contains the source notices that must accompany a Studio publication. The four donor notices are exact copies of the `LICENSE` file at the pinned submodule commit recorded below. The pinned donor checkouts remain in the repository as source evidence; no donor executable or binary-only helper is packaged by Studio.

| Donor | Repository | Pinned commit | License copyright | Notice file |
|---|---|---|---|---|
| FolderThumbnailFix | `https://github.com/LesFerch/FolderThumbnailFix` | `7f845506e088a6b1eb17ed867cc9882b52c17d3c` | Copyright (c) 2025 LesFerch | `FolderThumbnailFix-LICENSE.txt` |
| RightClickTools | `https://github.com/LesFerch/RightClickTools` | `f68de3f4854e53da0e78f22d8577330d0964d0d6` | Copyright (c) 2024 LesFerch | `RightClickTools-LICENSE.txt` |
| SetFolderType | `https://github.com/LesFerch/SetFolderType` | `cdde0ee160494a820e520b47a6d135562397e965` | Copyright (c) 2023 LesFerch | `SetFolderType-LICENSE.txt` |
| WinSetView | `https://github.com/LesFerch/WinSetView` | `fc4051c35cd5295ab6f7922539d930c0309638d3` | Copyright (c) 2021 Les Ferch | `WinSetView-LICENSE.txt` |

The `WIL-LICENSE.txt` and `WIL-ThirdPartyNotices.txt` files preserve the notices for the Microsoft.Windows.ImplementationLibrary `1.0.260126.7` package tracked under `RightClickTools-main/packages` at the pinned RightClickTools commit. They are retained as donor dependency provenance. The current Studio implementation does not reference WIL or the donor package; if WIL or any listed third-party component is reused, its notice is already available for the publication.

Studio uses native Windows resource APIs for the resource editor and does not redistribute Resource Hacker, SetACL, DT2DC, ViVeTool, Albacore.ViVe, or other donor `AppParts` binaries. The donor README claim that Resource Hacker is included does not provide a license grant in this checkout, so no such binary is copied into a publication.

The managed Studio and ToolHost projects copy this directory to both build output and publish output with `CopyToPublishDirectory=PreserveNewest`. This keeps notices beside each self-contained publication.
