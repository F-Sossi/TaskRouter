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

        mermaid.initialize({
            startOnLoad: false,
            securityLevel: 'strict',
            theme: dark ? 'dark' : 'default',
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
