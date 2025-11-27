const nativeInputSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;

class HtmlEvents extends Emitter {
    constructor() {
        super();
        this._watched = {
            input: new WeakSet(),
            keypress: new WeakSet(),
            keydown: new WeakSet()
        };

        this._onMouse = (ev) => {
            if (ev.selfEmit || ev.button !== 0) return;

            let el = ev.target;
            while (el && !el.id) el = el.parentNode;
            if (!el) return;

            this.dispatchEvent('emit', {type:'mouseup', id: el.id});
        };
        this._onInput = (ev) => {
            if (ev.selfEmit) return;
            this.dispatchEvent('emit', {type: 'input', id: ev.target.id, value: ev.target.value});
        }
        this._onKeypress = (ev) => {
            console.log('keypress', ev.keyCode);
            if (ev.selfEmit) return;
            this.dispatchEvent('emit', {type: 'keypress', id: ev.target.id, value: ev.keyCode});
        };
        this._onKeydown = (ev) => {
            if (ev.selfEmit) return;
            this.dispatchEvent('emit', {type: 'keydown', id: ev.target.id, value: ev.keyCode});
        };

        document.addEventListener('mouseup', this._onMouse, false);
        document.querySelectorAll('*').forEach(el => this._initElement(el));

        new MutationObserver(muts => {
            muts.forEach(m => m.addedNodes.forEach(n => {
                if (n.nodeType === 1) {
                    this._initElement(n);
                    n.querySelectorAll('*').forEach(el => this._initElement(el));
                }
            }));
        }).observe(document.documentElement, {childList:true,subtree:true});
    }

    /**
     * Static entrypoint used to replay events back into the DOM
     * (simulating user input on a specific element by id).
     *
     * @param {'mouseup'|'input'|'keypress'|'keydown'} type
     * @param {string} id
     * @param {string|number} [value]
     */
    static dispatch(type, id, value) {
        const el = document.getElementById(id);
        if (!el) return;
        switch (type) {
            case 'mouseup':
                ['mousedown', 'mouseup', 'click']
                    .forEach(evt => el.dispatchEvent(new MouseEvent(evt, {bubbles: true, cancelable: true, selfEmit: true})));
                break;
            case 'input':
                nativeInputSetter.call(el, value);
                el.dispatchEvent(new InputEvent('input', {bubbles: true, data: value, selfEmit: true}));
                break;
            case 'keypress':
                el.dispatchEvent(new KeyboardEvent('keypress', {bubbles: true, keyCode: value, selfEmit: true}));
                break;
            case 'keydown':
                el.dispatchEvent(new KeyboardEvent('keydown', {bubbles: true, keyCode: value, selfEmit: true}));
                break;
        }
    }

    _initElement(el) {
        if (!(el instanceof HTMLElement)) return;
        this._assignId(el);
        this._attachListeners(el);
    }

    _assignId(el) {
        if (el.id) return;

        const pathParts = [];
        let node = el;
        while (node && node.nodeType === 1) {
            let index = 0;
            let sib = node;
            while ((sib = sib.previousElementSibling)) index++;
            pathParts.push(node.tagName + ':' + index);
            node = node.parentElement;
        }
        pathParts.reverse();

        const attrs = el.getAttributeNames()
            .sort()
            .map(name => `${name}=${el.getAttribute(name)}`)
            .join('|');

        const str = pathParts.join('/') + '|' + attrs;
        let hash = 0;
        for (let i = 0; i < str.length; i++) {
            hash = ((hash << 5) - hash) + str.charCodeAt(i);
            hash |= 0;
        }

        let id = 'fsc_' + Math.abs(hash);
        while (document.getElementById(id)) {
            id += '1';
        }

        el.id = id;
    }

    _attachListeners(el) {
        const attr = el.getAttribute('fsc-listeners');
        const has = type => attr ? attr.split(',').includes(type) : false;
        const subscribe = (type, handler) => {
            const bucket = this._watched[type];
            if (!bucket || bucket.has(el)) return;
            bucket.add(el);
            el.addEventListener(type, handler, false);
        };

        if (el instanceof HTMLInputElement) {
            subscribe(el, 'input', this._onInput);
            return;
        }

        if (has('keypress')) subscribe('keypress', this._onKeypress);
        if (has('keydown')) subscribe('keydown', this._onKeydown);
    }
}
