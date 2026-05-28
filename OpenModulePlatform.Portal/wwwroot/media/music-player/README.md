# Portal Dashboard Music Player

The built-in Portal dashboard music player stores its shared playlist metadata
and MP3 media in the Portal database. This folder only documents the legacy
static playlist format and provides a small public metadata sample for creating
import zips. Runtime media must not be shipped inside the Portal web artifact,
because web artifacts are replaced during upgrades.

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

When `playlist.json` is used inside an admin import zip, relative `src` values
refer to MP3 files in the same zip. `url` can be used for externally hosted
audio metadata, but the built-in admin importer stores uploaded MP3 binaries in
`omp_portal.widget_binary_data` and rewrites playback URLs server-side.

The zip importer also accepts a `Songs.txt` file with repeated blocks in this
shape:

```text
Music track: Track name by Artist
Source: https://example.com/music
License or usage description
```

## Local Development

Keep MP3 files outside the public OpenModulePlatform repository. For local
testing, sign in as a Portal admin, add the music player widget, open its music
library button, and upload MP3 files or a zip containing MP3 files plus
`playlist.json` or `Songs.txt`.

The widget still works when the database playlist is empty. It shows the
empty-state label and lets the user add local MP3 files in the browser session
through the file picker or drag-and-drop. Browser-added files are kept as
client-side object URLs and are never uploaded to the server.

## Deployment And Packages

MP3 files should travel through Portal admin upload or a universal package
`widget-data/` object that writes to `omp_portal.widget_data` and
`omp_portal.widget_binary_data`. They should not travel as files in the Portal
artifact folder.

The dashboard widget definition itself is separate from the media files. Widget
metadata can be exported and imported through Portal or universal packages under
`widgets/`. When exporting a universal package from Portal, enable
`Include runtime data for selected widgets` to add a `widget-data/*.zip` object
with the playlist JSON and MP3 binaries. Importers remap `binaryDataId` values
and prefer `binaryDataHash` references when ids are not known yet, so the
package can move between installations safely.
