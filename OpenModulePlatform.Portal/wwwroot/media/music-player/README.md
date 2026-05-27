# Portal Dashboard Music Player

The built-in Portal dashboard music player reads `playlist.json` from this
folder. The public repository intentionally stores only metadata, not MP3
files, so fresh clones can show the widget and playlist format without carrying
binary media.

## Playlist Format

`playlist.json` uses a top-level `tracks` array. Each track needs either `src`
or `url`; the other fields are display and attribution metadata.

```json
{
  "tracks": [
    {
      "title": "Track name",
      "artist": "Artist",
      "src": "track-name.mp3",
      "attribution": "Music track: Track name by Artist",
      "source": "https://example.com/music",
      "description": "License or usage description"
    }
  ]
}
```

Relative `src` values are resolved beside `playlist.json`, so `track-name.mp3`
means `wwwroot/media/music-player/track-name.mp3` in the deployed Portal web
app. `url` can be used for an absolute or externally hosted audio file.

## Local Development

Keep MP3 files outside the public OpenModulePlatform repository. For local
testing, copy the private test files from the private installation repository
into this folder after cloning:

```powershell
Copy-Item "<workspace>\DEV\MP3\*.mp3" -Destination ".\OpenModulePlatform.Portal\wwwroot\media\music-player"
```

The widget still works when the server playlist or MP3 files are missing. It
shows the empty-state label and lets the user add local MP3 files in the browser
session through the file picker or drag-and-drop. Browser-added files are kept
as client-side object URLs and are never uploaded to the server.

## Deployment And Packages

MP3 files should travel through private installation material, not through the
public OpenModulePlatform repository. For customer or host-specific media, use
the private installer host profile, a config overlay, or another universal
package object that places the files beside the deployed Portal playlist.

The dashboard widget definition itself is separate from the media files. Widget
metadata can be exported and imported through Portal or universal packages under
`widgets/`, while the MP3 files remain deployment-owned media.
