// Renders Mermaid source into an element, for WorkflowDiagram.razor.
//
// Loaded as an ES module by Blazor's JS interop, but Mermaid itself is a plain script
// that assigns globalThis.mermaid, so this module injects the script tag and waits for
// it rather than importing it. That keeps the vendored file usable as-is, with no
// bundler step in the build.

// The package's own name. This said Workflow.MudBlazor until 2026-09-30, left behind by
// the rename -- so the script 404'd and the diagram fell back to showing its source. It
// went unnoticed because the first host loads a copy of Mermaid itself, which sets
// globalThis.mermaid before this module ever looks.
const MERMAID_SRC = '_content/TaskRouter.Blazor/lib/mermaid/mermaid.min.js';

let loading = null;

function loadMermaid() {
    if (globalThis.mermaid) {
        return Promise.resolve(globalThis.mermaid);
    }

    // One in-flight load no matter how many diagrams ask at once.
    loading ??= new Promise((resolve, reject) => {
        const existing = document.querySelector(`script[src="${MERMAID_SRC}"]`);
        const script = existing ?? document.createElement('script');

        script.addEventListener('load', () => resolve(globalThis.mermaid));
        script.addEventListener('error', () => {
            loading = null;
            reject(new Error('Could not load ' + MERMAID_SRC));
        });

        if (!existing) {
            script.src = MERMAID_SRC;
            document.head.appendChild(script);
        }
    });

    return loading;
}

// Rendering can fail on syntax Mermaid dislikes. The caller gets the message so the
// component can show the source instead of an empty box.
export async function render(element, id, source, dark) {
    if (!element) {
        return 'The diagram element is gone.';
    }

    try {
        const mermaid = await loadMermaid();

        // theme 'base' rather than 'dark'/'default', because those two ship opposite
        // palettes and the diagram then changes character with the page. 'base' imposes
        // nothing, so what follows is the whole palette -- and the node fills, which are
        // generated server-side where the theme is unknown, are saturated with white
        // labels precisely so they read against either of these backgrounds.
        //
        // Only the connective tissue is theme-dependent: lines and edge labels sit on the
        // page, not on a node, so they are the one part that has to know.
        mermaid.initialize({
            startOnLoad: false,
            securityLevel: 'strict',
            theme: 'base',
            themeVariables: {
                background: 'transparent',
                fontFamily: 'Segoe UI, Roboto, Helvetica, Arial, sans-serif',
                fontSize: '14px',

                // The app's own primary, so the diagram belongs to the page it is on.
                primaryColor: '#776BE7',
                primaryBorderColor: '#594AE2',
                primaryTextColor: '#ffffff',

                lineColor: dark ? '#b0b0b0' : '#6b7280',
                textColor: dark ? '#e8e8ef' : '#1f2430',
                edgeLabelBackground: dark ? '#262633' : '#f0f0f0',

                // Must track the page, not the nodes. Mermaid styles node labels and
                // edge labels with one rule -- `.label text, span { fill: nodeTextColor }`
                // -- and nodeTextColor otherwise falls back to primaryTextColor, which is
                // white here so labels read on the saturated fills. The nodes keep that
                // white through their own classDef, which is more specific; the edge
                // labels have no class, so they took the fallback and came out white on
                // the light edgeLabelBackground above -- invisible on a light page.
                nodeTextColor: dark ? '#e8e8ef' : '#1f2430'
            },
            flowchart: { useMaxWidth: true, htmlLabels: false }
        });

        const { svg } = await mermaid.render(id, source);
        element.innerHTML = svg;
        return null;
    } catch (error) {
        // Mermaid leaves its scratch element behind when a parse throws.
        document.getElementById('d' + id)?.remove();
        element.innerHTML = '';
        return error?.message ?? String(error);
    }
}
