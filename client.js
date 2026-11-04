// dsh-shot — client half: a "截图" button in conversation.input.left.
// Clicking it triggers the Host-side /shot command (registered by index.js),
// which captures a screenshot and feeds the image into the current session.
//
// Trigger mechanism (layered, first working channel wins):
//   1. ctx.remote.commands.execute(agent, line, attachments, signal)
//      - ctx.remote is the official client->Host remote face; official client
//        plugins declare "remote" (and namespaced faces like
//        "remote.directoryPicker") in their inject list
//        (@deepseek-ai/dsh-client-ui-workspace lib/client.js:5005-5015,
//        5039: `ctx.remote.directoryPicker`).
//      - The Host `commands` service is a TypertRemoteService ("commands")
//        whose @Remote execute(agent, line, submittedAttachments, signal)
//        parses and runs a slash command without a model turn
//        (@deepseek-ai/dsh-commands lib/index.js:210-330; confirmed live via
//        host inspect Service.listService catalog: "@Remote async execute(...)".
//        NOTE: the wire shape of the first `agent` parameter from a browser
//        client is not fully confirmed from shipped sources; we pass
//        { session: sessionId } and fall through to the next channel on any
//        failure or an undefined (admission-miss) result.
//   2. inputActions insert/draft probe: if the composer's inputActions prop
//      exposes an insert/draft method, write "/shot" into the input so the
//      user presses Enter (the normal slash pipeline then runs the command).
//   3. No channel: console.error('no host channel') and keep the button
//      registered (visible) — functionality stays a TODO as instructed.
// Captured in apply(); declared before load() so the factory body can assign it.
var moduleScopeRemoteExecute = null;
// Draft inserter (set in apply): consumes one completed-shot payload into the composer
// draft of the given session. Images go through conversation.createDrafts, so they stay
// pending until the user presses Enter — never auto-submitted.
var moduleScopeInsertShot = null;
// Host poll state: monotonic shot sequence + the payload awaiting draft insertion.
var pendingShot = null;
var shotSeq = 0;

window.__ModuleLoader__.load({
  // Registration id MUST equal the package name (dsh-shot): the client-modules
  // loader keys graph rows by package name and checks factories.has(<row id>)
  // after executing this bundle. A mismatched id (e.g. "@local/dsh-shot")
  // reads as "executed without registering", triggers the fallback re-execution,
  // and fails the whole web boot with a duplicate-registration error.
  id: 'dsh-shot',
  factory(require) {
    const React = require('react');
    const h = React.createElement;

    const SHOT_COMMAND = '/shot';

    // Camera glyph, hand-written SVG (no icon library dependency).
    function CameraIcon() {
      return h('svg', {
        viewBox: '0 0 24 24', width: 16, height: 16,
        fill: 'none', stroke: 'currentColor', strokeWidth: 1.6,
        strokeLinecap: 'round', strokeLinejoin: 'round',
        'aria-hidden': true, focusable: false,
        style: { display: 'block' },
      },
        h('path', { d: 'M4 8.5A1.5 1.5 0 0 1 5.5 7h2l1.2-1.8A1.5 1.5 0 0 1 9.95 4.5h4.1a1.5 1.5 0 0 1 1.25.7L16.5 7h2A1.5 1.5 0 0 1 20 8.5v9A1.5 1.5 0 0 1 18.5 19h-13A1.5 1.5 0 0 1 4 17.5v-9Z' }),
        h('circle', { cx: 12, cy: 13, r: 3.2 })
      );
    }

    function ShotButton(props) {
      const [busy, setBusy] = React.useState(false);
      const sessionId = props && props.sessionId;
      const inputActions = props && props.inputActions;

      // Consume completed shots (from the composer button, /shot, or the global hotkey)
      // and insert them into this session's composer draft. Requires no user action:
      // the image waits in the draft until Enter is pressed.
      React.useEffect(function () {
        const timer = setInterval(function () {
          if (!pendingShot || !moduleScopeInsertShot) return;
          const sid = props && props.sessionId;
          if (!sid) return;
          const ps = pendingShot;
          pendingShot = null;
          setBusy(true);
          Promise.resolve(moduleScopeInsertShot(ps, sid, inputActions))
            .catch(function (error) {
              console.error('[dsh-shot] draft insert failed:', error);
            })
            .finally(function () { setBusy(false); });
        }, 700);
        return function () { clearInterval(timer); };
      }, []);

      const onClick = () => {
        if (busy) return;
        setBusy(true);
        runShot(sessionId, inputActions).catch((error) => {
          console.error('[dsh-shot] 截图触发失败:', error);
        }).finally(() => {
          setBusy(false);
        });
      };

      return h('button', {
        type: 'button',
        title: '截图',
        'aria-label': '截图',
        disabled: busy,
        onClick,
        style: {
          display: 'inline-flex',
          alignItems: 'center',
          justifyContent: 'center',
          border: 'none',
          background: 'transparent',
          color: busy ? 'var(--dsw-alias-label-tertiary)' : 'var(--dsw-alias-label-secondary)',
          cursor: busy ? 'default' : 'pointer',
          borderRadius: 'var(--dsw-radius-md, 6px)',
          width: 28,
          height: 28,
          padding: 0,
        },
        onMouseEnter: (ev) => { if (!busy) ev.currentTarget.style.background = 'var(--dsw-alias-interactive-bg-hover)'; },
        onMouseLeave: (ev) => { ev.currentTarget.style.background = 'transparent'; },
      }, h(CameraIcon));
    }

    async function runShot(sessionId, inputActions) {
      // --- Channel 0 (primary): direct capture trigger on the Host ---
      // Mirrors the global-hotkey flow: POST /dsh-shot/capture runs the capture
      // helper, stores the shot, and the poller below inserts it into the draft.
      try {
        const r = await fetch('/dsh-shot/capture', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({}),
        });
        const j = await r.json().catch(() => null);
        if (r.ok && j && j.ok) return j;
        console.warn('[dsh-shot] capture route failed:', r.status, j && j.error, '; trying fallback channels');
      } catch (error) {
        console.warn('[dsh-shot] capture route unavailable; trying fallback channels:', error);
      }

      // --- Channel 1: direct Host command execution ---
      const execute = moduleScopeRemoteExecute;
      if (typeof execute === 'function') {
        try {
          const result = await execute({ session: sessionId }, SHOT_COMMAND, [], undefined);
          // Host admission miss (unknown command / bad agent) resolves undefined.
          if (result !== undefined) return result;
          console.warn('[dsh-shot] remote.commands.execute returned undefined; trying fallback channels');
        } catch (error) {
          console.warn('[dsh-shot] remote.commands.execute failed; trying fallback channels:', error);
        }
      }

      // --- Channel 2: composer inputActions insert/draft probe ---
      if (inputActions && typeof inputActions === 'object') {
        for (const name of ['insertText', 'insert', 'setDraft', 'setText']) {
          const fn = inputActions[name];
          if (typeof fn === 'function') {
            try {
              fn.call(inputActions, SHOT_COMMAND);
              console.info(`[dsh-shot] inserted ${SHOT_COMMAND} via inputActions.${name}; press Enter to run it`);
              return true;
            } catch (error) {
              console.warn(`[dsh-shot] inputActions.${name} failed:`, error);
            }
          }
        }
      }

      // --- Channel 3: no usable channel ---
      console.error('[dsh-shot] no host channel',
        JSON.stringify({
          hasRemoteExecute: typeof execute === 'function',
          inputActionsKeys: inputActions && typeof inputActions === 'object' ? Object.keys(inputActions) : null,
          sessionId: sessionId ?? null,
        }));
      throw new Error('no host channel for /shot');
    }

    return {
      inject: ['slots', 'sessions', 'remote', 'conversation'],
      apply(ctx) {
        // Capture the remote face for the click handler. The commands face is
        // namespaced under ctx.remote.commands (the Host service registers as
        // TypertRemoteService "commands"); guard every access so a profile
        // without it degrades to the fallback channels instead of throwing.
        let remoteFace = null;
        try {
          const remote = ctx.remote;
          const commands = remote && remote.commands;
          if (commands && typeof commands.execute === 'function') {
            remoteFace = (agent, line, attachments, signal) => commands.execute(agent, line, attachments, signal);
          }
        } catch (error) {
          console.warn('[dsh-shot] ctx.remote unavailable at apply:', error);
        }
        moduleScopeRemoteExecute = remoteFace;

        // Draft inserter: turns one completed-shot payload (base64 PNG + action) into a
        // pending draft attachment (+ preset caption for translate/ocr) in the given session.
        moduleScopeInsertShot = async function (shot, sessionId, inputActions) {
          const bin = atob(shot.dataB64);
          const arr = new Uint8Array(bin.length);
          for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
          const file = new File([arr], shot.name || 'screenshot.png', { type: 'image/png' });

          const conversation = ctx.get('conversation');
          if (!conversation || typeof conversation.createDrafts !== 'function') {
            throw new Error('conversation service (createDrafts) unavailable');
          }
          const drafts = conversation.createDrafts(sessionId, [file]);
          const ids = (drafts || []).map((d) => d.id);

          const actions = (inputActions && typeof inputActions.addAttachments === 'function')
            ? inputActions
            : (((conversation.input && conversation.input.shell && conversation.input.shell(sessionId)) || {}).actions);
          if (!actions || typeof actions.addAttachments !== 'function') {
            throw new Error('no addAttachments API on the composer input');
          }
          if (ids.length) actions.addAttachments(ids);

          const captions = {
            translate: '请翻译这张截图里的文字内容。',
            ocr: '请识别并提取这张截图中的全部文字，保持原有排版。',
          };
          const caption = captions[shot.action];
          if (caption && typeof actions.setDraft === 'function') actions.setDraft(caption);
          return { inserted: ids.length, action: shot.action || 'session' };
        };

        // Poll the localhost route the Host half serves; completed shots queue as
        // pendingShot and the slot component inserts them into the current draft.
        ctx.effect(function () {
          const timer = setInterval(function () {
            fetch('/dsh-shot/last?since=' + shotSeq)
              .then((r) => r.json())
              .then((j) => {
                if (j && !j.none && j.seq > shotSeq) {
                  shotSeq = j.seq;
                  pendingShot = j;
                }
              })
              .catch(function () {});
          }, 1200);
          return function () { clearInterval(timer); };
        });

        ctx.slots.inject('conversation.input.left', () => ctx.slots.register({
          name: 'conversation.input.left',
          id: 'dsh-shot-button',
          order: 5,
          title: '截图',
          description: 'Capture a screenshot and attach it to the conversation (/shot)',
        }, ShotButton));
      },
    };
  },
});
