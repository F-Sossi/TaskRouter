# Vendored dependency

`mermaid.min.js` is Mermaid 11.16.1, taken unmodified from
https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js

Mermaid is MIT licensed. Its licence and those of its bundled dependencies are
included in the file's own header comments.

**Why vendored rather than loaded from a CDN:** consumers of this library may run
air-gapped, and a diagram that silently fails to render on a disconnected network is
worse than a larger repository. The file exports `globalThis.mermaid`, so a plain
script tag is enough -- no module loader involved.

To update: replace the file, change the version above, and check that
`WorkflowDiagram.razor` still renders -- the render API has changed between major
versions before.
