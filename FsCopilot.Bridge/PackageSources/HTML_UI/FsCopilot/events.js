const nativeInputSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;

class HtmlEvents extends Emitter {
    constructor() {
        super();
        
        if (!HtmlEvents._id2El) {
            HtmlEvents._id2El = new Map();
            HtmlEvents._el2Id = new Map();
        }
        
        this._watched = {
            input: new WeakSet(),
            keypress: new WeakSet(),
            keydown: new WeakSet()
        };

        this._onMouse = (ev) => {
            if (ev.selfEmit || ev.button !== 0) return;

            let el = ev.target;
            while (el && !HtmlEvents._el2Id.get(el)) el = el.parentNode;
            if (!el) return;
            this.dispatchEvent('emit', {type:'mouseup', id: HtmlEvents._el2Id.get(el)});
        };
        this._onInput = (ev) => {
            if (ev.selfEmit) return;
            this.dispatchEvent('emit', {type: 'input', id: HtmlEvents._el2Id.get(ev.target), value: ev.target.value});
        }
        this._onKeypress = (ev) => {
            console.log('keypress', ev.keyCode);
            if (ev.selfEmit) return;
            this.dispatchEvent('emit', {type: 'keypress', id: HtmlEvents._el2Id.get(ev.target), value: ev.keyCode});
        };
        this._onKeydown = (ev) => {
            if (ev.selfEmit) return;
            this.dispatchEvent('emit', {type: 'keydown', id: HtmlEvents._el2Id.get(ev.target), value: ev.keyCode});
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
        const el = HtmlEvents._id2El.get(id);
        if (!el) return;
        let evt;
        switch (type) {
            case 'mouseup':
                ['mousedown', 'mouseup', 'click']
                    .forEach(evType => {
                        evt = new MouseEvent(evType, {bubbles: true, cancelable: true});
                        evt.selfEmit = true;
                        el.dispatchEvent(evt);
                    });
                break;
            case 'input':
                nativeInputSetter.call(el, value);
                evt = new InputEvent('input', {bubbles: true, data: value})
                evt.selfEmit = true;
                el.dispatchEvent(evt);
                break;
            case 'keypress':
                evt = new KeyboardEvent('keypress', {bubbles: true, keyCode: value});
                evt.selfEmit = true;
                el.dispatchEvent(evt);
                break;
            case 'keydown':
                evt = new KeyboardEvent('keydown', {bubbles: true, keyCode: value});
                evt.selfEmit = true;
                el.dispatchEvent(evt);
                break;
        }
    }

    _initElement(el) {
        if (!(el instanceof HTMLElement)) return;
        this._assignId(el);
        this._attachListeners(el);
    }

    _assignId(el) {
        if (HtmlEvents._el2Id.has(el)) return;

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

        // const attrs = el.getAttributeNames()
        //     .filter(name => name !== 'id')
        //     .sort()
        //     .map(name => `${name}=${el.getAttribute(name)}`)
        //     .join('|');

        // const str = pathParts.join('/') + '|' + attrs;
        const str = pathParts.join('/');
        let hash = 0;
        for (let i = 0; i < str.length; i++) {
            hash = ((hash << 5) - hash) + str.charCodeAt(i);
            hash |= 0;
        }

        let id = 'fsc_' + Math.abs(hash);
        while (document.getElementById(id)) {
            id += '1';
        }

        // el.id = id;
        HtmlEvents._el2Id.set(el, id);
        HtmlEvents._id2El.set(id, el);
    }

    _attachListeners(el) {
        const store = window.fscListeners;

        const has = (type) => {
            if (!store) return false;
            const set = store.get(el);
            return !!set && set.has(type);
        };

        const subscribe = (type, handler) => {
            const bucket = this._watched[type];
            if (!bucket || bucket.has(el)) return;
            bucket.add(el);
            el.addEventListener(type, handler, false);
        };

        if (el instanceof HTMLInputElement) {
            subscribe('input', this._onInput);
            return;
        }

        if (has('keypress')) subscribe('keypress', this._onKeypress);
        if (has('keydown')) subscribe('keydown', this._onKeydown);
    }
}
