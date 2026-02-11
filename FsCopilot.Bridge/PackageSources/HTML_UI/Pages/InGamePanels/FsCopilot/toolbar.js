// https://github.com/parallel42/msfs-toolbar-interop
(function () {
    Include.addImports(['/FsCopilot/common.js'], () =>
    Include.addImports(['/FsCopilot/bus.js'], initialize));

    function initialize() {
        console.log('[FsCopilot] [Toolbar] Initialize');
        show_toolbar(false);
        // let bus = new Bus();
        // bus.addEventListener('open', () => { show_toolbar(true); });
        // bus.addEventListener('close', () => { show_toolbar(false); });
        // show_toolbar(bus.connected);
    }

    function show_toolbar(show) {
        // Find the Toolbar (MSFS 2024)
        let toolbar = document.querySelector('ui-panel');
        let toolbar_button;

        if(toolbar) {
            toolbar_button = toolbar.querySelector('ui-resource-element[icon="coui://html_ui/vfs/html_ui/icons/toolbar/FSCOPILOT_TOOLBAR_ICON.svg"]');

        } else {
            // Find the Toolbar (MSFS 2020)
            toolbar = document.querySelector('tool-bar');
            toolbar_button = toolbar.querySelector('toolbar-button[panel-id="PANEL_FS_COPILOT"]');
        }

        if (!show) toolbar_button.style.display = 'none';
        else toolbar_button.style.display = 'block';
    }
})();