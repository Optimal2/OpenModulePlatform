# Blank Widget Images

The Portal ships two small static GIF variants for the built-in blank dashboard
widget:

- `1.gif`
- `2.gif`

These files are compatibility defaults only. Administrator-uploaded images and
GIF files are stored in `omp_portal.widget_data` and
`omp_portal.widget_binary_data`, not in this web artifact folder. This prevents
Portal upgrades from removing custom dashboard media when the artifact folder is
replaced.

Portal admins can upload custom blank-widget media from the widget settings
panel on the dashboard. A ZIP import can include `.gif`, `.png`, `.jpg`, or
`.jpeg` files and an optional `images.json` file:

```json
{
  "images": [
    {
      "fileName": "example.gif",
      "displayName": "Example animation"
    }
  ]
}
```

Universal packages can also carry shared blank-widget runtime data. Export the
blank widget definition from Portal and enable `Include runtime data for
selected widgets` to add a `widget-data/*.zip` object with the image-list JSON
and binary media. Importers remap source `binaryDataId` values to the target
database, so the package can be moved between installations without storing
custom media in the Portal web artifact.
