# Photo Viewer Lite

A small single-window picture viewer modelled on Windows Photo Viewer. One C# file, built with the
C# compiler that ships with .NET Framework 4.8, so no SDK or Visual Studio is needed.

```powershell
.\build.ps1
.\bin\PhotoViewerLite.exe "C:\path\to\picture.jpg"
```

## Installing

`build.ps1` also produces `dist\PhotoViewerLite-Setup.exe`, a single-file installer with the viewer
embedded in it. It installs for your account only, so no administrator rights are needed:

- The app goes in `%LOCALAPPDATA%\Programs\PhotoViewerLite`, with a Start menu shortcut and an
  optional desktop shortcut.
- It's added to "Open with" for picture files and appears in Settings → Default apps, so you can
  make it the default. Your current default app isn't changed.
- It appears in Settings → Apps → Installed apps, where Uninstall removes all of the above.

From the command line: `PhotoViewerLite-Setup.exe /quiet` installs silently, and
`Uninstall.exe /uninstall [/quiet]` (in the install folder) removes it. Setup and uninstall both
refuse to run while the viewer is open.

## How it decodes

It follows the same Windows Imaging Component (WIC) steps as `PhotoViewer.dll` and `ImagingEngine.dll`:

1. Open the file as a read-only stream and create a decoder from it.
2. Use frame 0, or step through pages of multi-page TIFF/GIF files.
3. Show the embedded thumbnail first, then the full picture.
4. Apply the EXIF orientation tag.
5. Convert an embedded colour profile to sRGB. If the profile can't be used, show the raw pixels.

Any format with a WIC codec installed opens (JPEG, PNG, BMP, GIF, TIFF, ICO, JPEG XR, and HEIC,
AVIF, WebP or camera RAW when their codec extensions are installed).

## Keys and mouse

| Action | Keys / mouse |
|---|---|
| Previous / next picture in the folder | Left / Right (wraps around), Home / End |
| Previous / next page | Page Up / Page Down |
| Zoom | Mouse wheel, + / - |
| Fit to window / actual size | Ctrl+0 / Ctrl+1, or double-click to toggle |
| Pan | Drag |
| Rotate counterclockwise / clockwise | Ctrl+, / Ctrl+. |
| Slide show | F11 or F5; Esc or a click exits |
| Open, copy, print | Ctrl+O, Ctrl+C, Ctrl+P |
| Delete (to the Recycle Bin, with confirmation) | Del |

Pictures can also be dropped onto the window.

## Rotation

Like Photo Viewer, a rotation is saved when you move to another picture or close the window.

- **JPEG and TIFF:** the EXIF orientation tag is updated in place, which leaves the image data
  untouched. If a JPEG has no room for the tag, you're asked before it's re-saved at quality 95.
  That re-save adds padding, so later rotations of the same file are lossless.
- **PNG, BMP and GIF:** the pixels are rotated and the file is rewritten. These formats are
  lossless, so nothing is lost.
- **Other formats and multi-page files:** rotation only changes the view.

Files are written to a temporary file next to the original and then swapped in, so a failed save
leaves the original intact.
