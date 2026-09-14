import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';
import assert from 'node:assert/strict';

// Execute the production bootstrap, rather than a separately maintained JS copy.
const source = readFileSync(new URL('../src/NativeWebView/MacOSWebMessageBridge.cs', import.meta.url), 'utf8');
const bootstrap = source.match(/Bootstrap = """\r?\n([\s\S]*?)\r?\n\s*""";/)[1];
// Use the production dispatch wrappers; C# tests verify the serialized string argument.
const jsonWrapper = source.match(/payload = "([^"]+)" \+ JsonSerializer.Serialize\(message\) \+ "([^"]+)";/);
const dispatchWrapper = source.match(/return "([^"]+)" \+ payload \+ "([^"]+)";/);
const createPage = () => {
  const posted = [];
  const page = { EventTarget, MessageEvent, webkit: { messageHandlers: {
    nativeWebViewMessage: { postMessage: value => posted.push(JSON.parse(value)) }
  } } };
  runInNewContext(bootstrap, page);
  return { page, posted };
};

test('page messages preserve strings and structured JSON', () => {
  const { page, posted } = createPage();
  const value = '  quotes " \\ 雪\n  ';
  page.chrome.webview.postMessage(value);
  page.chrome.webview.postMessage({ value });
  assert.deepEqual(posted, [
    { nativeWebViewVersion: 1, kind: 'string', payload: value },
    { nativeWebViewVersion: 1, kind: 'json', payload: JSON.stringify({ value }) }
  ]);
});

test('host replies reach listeners without echoing into the host', () => {
  const { page, posted } = createPage();
  const received = [];
  const listener = event => received.push(event.data);
  page.chrome.webview.addEventListener('message', listener);
  const response = { requestId: 'request', value: '  secret\n雪  ' };
  page.__nativeWebViewDispatchMessage(response);
  page.chrome.webview.removeEventListener('message', listener);
  page.__nativeWebViewDispatchMessage('ignored');
  assert.deepEqual(received, [response]);
  assert.deepEqual(posted, []);
});

test('independent pages do not share listeners', () => {
  const first = createPage().page;
  const second = createPage().page;
  let count = 0;
  first.chrome.webview.addEventListener('message', () => count++);
  second.__nativeWebViewDispatchMessage('other session');
  assert.equal(count, 0);
});

test('JSON dispatch preserves prototype-named properties as ordinary data', () => {
  const { page, posted } = createPage();
  const json = '{"__proto__":{"marker":true},"nested":{"__proto__":null},"value":"quotes \\\" 雪"}';
  let received;
  page.chrome.webview.addEventListener('message', event => { received = event.data; });
  assert.ok(jsonWrapper, 'JSON dispatch must decode a string with JSON.parse');
  const script = dispatchWrapper[1] + jsonWrapper[1] + JSON.stringify(json) + jsonWrapper[2] + dispatchWrapper[2];
  runInNewContext(script, page);
  assert.equal(JSON.stringify(received), JSON.stringify(JSON.parse(json)));
  assert.equal(Object.hasOwn(received, '__proto__'), true);
  assert.equal(Object.hasOwn(received.nested, '__proto__'), true);
  assert.equal(received.marker, undefined);
  assert.equal(Object.getPrototypeOf(received), runInNewContext('Object.prototype', page));
  assert.deepEqual(posted, []);
});
