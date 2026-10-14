# Changelog

## 0.2.0 — 2026-10-14

- Disk Analysis for arbitrary folders and entire drives, independent of registered catalog locations.
- Background hierarchical scans with logical totals, extension statistics, throttled progress, cancellation and explicit incomplete results.
- Native Win2D squarified treemap, bounded rendering, spatial hit testing, selection, cached folder navigation and resizable panes.
- Name, extension, category and minimum-size filters; size/percentage, name and file-count sorting.
- Existing previews and explicit single-item file/folder tagging; library folder context action to analyze space.
- Confirmed single-file recycling with metadata and native identity checks; physical allocation remains explicitly unknown.
- Scanner, catalog isolation, geometry, million-entry layout and real filesystem edge-case checks; documented benchmarks and NTFS follow-up design.

## 0.1.1 — 2026-10-14

- File and folder context menus with native Windows Properties.
- Create library tags from the sidebar without selecting a file.
- Remove only tags assigned to the selected items.
- Delete a catalog tag and its file/folder links after confirmation, preserving disk contents.
- Stale item-tag undo skips tags that have been deleted from the catalog.

## 0.1.0 — 2026-10-13

- CleanTag branding and an unpackaged Windows x64 app.
- Inbox review with previews, keep/defer decisions and guarded deletion.
- SQLite library with file and folder tagging, aliases, tag colors and bulk undo.
- Compact gallery, source/tag navigation and contextual preview.
- Fast scanning, live monitoring, metadata backup and export.
- Consistent first-run migration from LookClean, preserving the original data.

This is an early release. The UI is currently Turkish and the executable is unsigned.
