# JumpSkip Jellyfin

JumpSkip Detects and skips media segments (intros, recaps, outros/credits, and previews) using both chapter titles and crowdsourced timestamp data from TheIntroDB, IntroDB, and SkipDB.

- Intro, recap, outro/credits and preview/post-credits segments.
- Provider Priority selectors, Merge segments, timing offsets and library exclusions.
- Chapter marking, add segment boundaries to navigation.
- Chapter-based fallback for gaps in provider timestamps.

> [!IMPORTANT]
> Requires **Jellyfin 12.4.0**.

> [!NOTE]
> Choose each segment's action in your client's **Settings → Playback → Media segments actions**.
> 
> **None / Ask To Skip / Skip**

## Install and configure
### **Automatic updates**:

1. Go to Dashboard > Plugins.
2. Click "Manage Repositories tab" at the top. Click the "+ New Repository" button.
3. Enter a Name and paste this Repository URL then click Add
 `https://raw.githubusercontent.com/JumpSkip/jellyfin-jumpskip/main/manifest.json`.
4. Go back to the Catalog tab. The new plugin will now appear in the available list.
5. Click Install.
6. Configure at **Dashboard → Plugins → JumpSkip**, and press **Save Settings**.

### **Manual**:

1. Download the latest plugin from the [Releases](https://github.com/JumpSkip/jellyfin-jumpskip/releases) page.
2. Extract `.zip` into your Jellyfin plugins folder:
3. Restart Jellyfin.
4. Configure at **Dashboard → Plugins → JumpSkip**, and press **Save Settings**.


| Installation | Example directory |
|---|---|
| Windows | `C:\ProgramData\Jellyfin\Server\plugins\JumpSkip_1.0.0.0` |
| Linux package | `/var/lib/jellyfin/plugins/JumpSkip_1.0.0.0` |
| Docker | `/config/plugins/JumpSkip_1.0.0.0` |

## Build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and PowerShell 7. Run from this folder:

```powershell
.\build.ps1
```

The build uses locked NuGet packages and writes only the install ZIP and its `.md5` and `.sha256` checksum files to `artifacts`. The ZIP contains exactly `JumpSkip.dll`, `meta.json` and `icon.svg`. The complete install `meta.json` is generated from the stable C# identity and embedded release metadata. Building leaves the source `meta.json` and repository `manifest.json` unchanged.
