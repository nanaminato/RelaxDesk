# UI clipping correction — 2026-11-26

The refreshed shared TextBox/PasswordBox templates applied Padding again as the content-host Margin. Removing that duplicated inset restores the usable text viewport. Viewer port width increases from 68 to 82 DIP, and collapsing options consistently returns to 480 DIP rather than 470. Window layout uses pixel rounding. Host main content has an explicitly bounded horizontal viewport, wrapped titles/state, and vertical scrolling at minimum size.

Release build with stable .NET 11.0.100 passed with zero warnings/errors. Viewer design verification passed 14 checks (including the last port digit fitting), preferences passed 21 checks, and Host layout verification passed connection/settings at 940 and 760 DIP width. PNG previews were visually inspected: complete 48200 port and bounded Host cards. These are WPF layout/render tests; physical multi-DPI/multi-monitor interaction remains manual coverage. Existing high-frame-rate/bitrate and unlimited file options are included. No new performance claim.

Commands:

```powershell
dotnet build RelaxDesk.sln -c Release -p:RelaxDeskNativePath=E:/RelaxDesk/build/native-high-rate/Release/RelaxDesk.Native.dll
dotnet src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll --design-contracts build/ui-fixed/viewer
dotnet src/RelaxDesk.HostShell/bin/Release/net11.0-windows/RelaxDesk.HostShell.dll --layout-contracts build/ui-fixed/host
dotnet src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll --preferences-contracts build/ui-fixed/preferences
```
