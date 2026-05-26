# Multi-Image Reference

Status: implemented for single-image generation.

## Behavior

- The current Rhino/source image is always request image 1.
- Active references are appended as request images 2..N.
- The source preview shows active reference thumbnails below the source image.
- Thumbnails are labeled `图2`, `图3`, etc. These labels match the API image order.
- The user can add active references from local files or from the saved reference library.
- Each active reference is copied into `%APPDATA%\AIRenderer\active-references\` when added.
- Removing one reference deletes its active temp copy when possible.
- Clearing references deletes all active temp copies when possible.

This active-copy behavior is intentional: deleting or moving a source file or deleting an item from the saved reference library must not break a reference that is already attached to the current request.

## Prompt Convention

The request prompt is automatically extended with image-order instructions:

- `image 1` / `图1`: source scene
- `image 2` / `图2`: first reference thumbnail
- `image 3` / `图3`: second reference thumbnail

Recommended user wording:

```text
Keep 图1's camera angle, room layout, and main geometry.
Use 图2 as the wood material reference.
Use 图3 as the lighting mood reference.
Generate a modern interior rendering.
```

## API Routing

- APIYI `gpt-image-2` normal generation uses `/v1/images/generations`.
- APIYI custom providers with `ApiFormat=openai` are routed to `/v1/images/generations` when the model is `gpt-image-2` and the host is `api.apiyi.com`, `vip.apiyi.com`, or `b.apiyi.com`.
- The payload `image` field is an array of base64 PNG images in source-then-reference order.

## Current Scope

Implemented:

- Single-image generation with multiple active references.
- Local-file reference add.
- Reference-library multi-select add.
- Per-reference remove.
- Clear all active references.
- Stable numbered prompt instructions.

Not implemented yet:

- Batch mode shared multi-reference collection.
- Reference reordering.
- Mask edit with additional reference images.
