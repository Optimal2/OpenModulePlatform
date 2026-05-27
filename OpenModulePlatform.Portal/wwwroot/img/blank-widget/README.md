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
