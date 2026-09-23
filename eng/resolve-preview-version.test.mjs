import assert from 'node:assert/strict';
import test from 'node:test';
import { NUGET_ORG, chooseVersion, historySources, readHistory, readVersions } from './resolve-preview-version.mjs';

test('first publish uses the configured preview; later publishes increment numerically', () => {
  assert.equal(chooseVersion('0.1.0-preview.1', []), '0.1.0-preview.1');
  assert.equal(chooseVersion('0.1.0-preview.1', ['0.1.0-preview.1']), '0.1.0-preview.2');
  assert.equal(chooseVersion('0.1.0-preview.1', ['0.1.0-preview.9', '0.1.0-preview.10']), '0.1.0-preview.11');
});

test('configured preview is a floor and other release lines do not affect it', () => {
  assert.equal(chooseVersion('0.1.0-preview.4', [
    '0.1.0-preview.1', '0.2.0-preview.99', '0.1.0', '0.1.0-rc.9',
  ]), '0.1.0-preview.4');
});

test('only unused previews on the configured release line are accepted', () => {
  const published = ['0.1.0-preview.1'];
  assert.equal(chooseVersion('0.1.0-preview.1', published, { suffix: 'preview.3' }), '0.1.0-preview.3');
  assert.equal(chooseVersion('0.1.0-preview.1', published, { tag: 'v0.1.0-preview.2' }), '0.1.0-preview.2');
  for (const suffix of ['preview.1', 'rc.2', 'preview.0', 'preview.02', 'preview.2;evil']) {
    assert.throws(() => chooseVersion('0.1.0-preview.1', published, { suffix }));
  }
  for (const tag of ['v0.1.0', 'v0.1.0-rc.2', 'v0.2.0-preview.2', 'v0.1.0-preview.1']) {
    assert.throws(() => chooseVersion('0.1.0-preview.1', published, { tag }));
  }
  assert.throws(() => chooseVersion('0.1.0', published));
});

function fakeFeed(responses) {
  return async (url, options) => {
    assert.ok(options.signal);
    if (url === 'https://feed/index.json') return Response.json({
      resources: [{ '@type': 'PackageBaseAddress/3.0.0', '@id': 'https://feed/flat/' }],
    });
    assert.ok(Object.hasOwn(responses, url), `Unexpected request ${url}`);
    const response = responses[url];
    return typeof response === 'number' ? new Response(null, { status: response }) : Response.json(response);
  };
}

test('all packages contribute, including a partially published newer preview', async () => {
  const versions = await readVersions('https://feed/index.json', ['Core', 'Provider', 'New'], {}, fakeFeed({
    'https://feed/flat/core/index.json': { versions: ['0.1.0-preview.1'] },
    'https://feed/flat/provider/index.json': { versions: ['0.1.0-preview.1', '0.1.0-preview.2'] },
    'https://feed/flat/new/index.json': 404,
  }));
  assert.equal(chooseVersion('0.1.0-preview.1', versions), '0.1.0-preview.3');
});

test('feed failures and malformed responses stop publication', async () => {
  for (const response of [401, 403, 429, 500, {}, { versions: [2] }]) {
    await assert.rejects(readVersions('https://feed/index.json', ['Core'], {}, fakeFeed({
      'https://feed/flat/core/index.json': response,
    })));
  }
  await assert.rejects(readVersions('https://feed/index.json', ['Core'], {}, async () => {
    throw new Error('Network unavailable');
  }));
  await assert.rejects(readVersions('https://feed/index.json', ['Core'], {}, async () => Response.json({})));
});

test('the next preview is cumulative across nuget.org and the retired GitHub feed', async () => {
  const sources = [
    { source: 'https://feed/index.json', headers: {} },
    { source: 'https://old/index.json', headers: { authorization: 'Basic x' } },
  ];
  // GitHub Packages holds preview.3 and nuget.org only preview.2: the next publish is preview.4.
  const feeds = {
    'https://feed/flat/core/index.json': { versions: ['0.1.0-preview.1', '0.1.0-preview.2'] },
    'https://old/flat/core/index.json': { versions: ['0.1.0-preview.1', '0.1.0-preview.2', '0.1.0-preview.3'] },
  };
  const fetchImpl = async (url, options) => {
    assert.ok(options.signal);
    if (url.startsWith('https://old/')) assert.equal(options.headers.authorization, 'Basic x');
    else assert.equal(options.headers.authorization, undefined);
    if (url.endsWith('.json') && url.split('/').length === 4) {
      const base = url.replace(/index\.json$/, 'flat/');
      return Response.json({ resources: [{ '@type': 'PackageBaseAddress/3.0.0', '@id': base }] });
    }
    return Response.json(feeds[url]);
  };
  const versions = await readHistory(sources, ['Core'], fetchImpl);
  assert.equal(chooseVersion('0.1.0-preview.1', versions), '0.1.0-preview.4');
  // Either feed alone being ahead decides it, and a requested number either feed used is refused.
  assert.equal(chooseVersion('0.1.0-preview.1', ['0.1.0-preview.5', '0.1.0-preview.3']), '0.1.0-preview.6');
  assert.throws(() => chooseVersion('0.1.0-preview.1', versions, { suffix: 'preview.3' }));
  assert.throws(() => chooseVersion('0.1.0-preview.1', versions, { tag: 'v0.1.0-preview.3' }));
});

test('a failure reading either feed stops publication', async () => {
  const sources = [
    { source: 'https://feed/index.json', headers: {} },
    { source: 'https://old/index.json', headers: {} },
  ];
  const fetchImpl = async url => {
    if (url.startsWith('https://old/')) return new Response(null, { status: 401 });
    if (url === 'https://feed/index.json') return Response.json({
      resources: [{ '@type': 'PackageBaseAddress/3.0.0', '@id': 'https://feed/flat/' }],
    });
    return Response.json({ versions: ['0.1.0-preview.1'] });
  };
  await assert.rejects(readHistory(sources, ['Core'], fetchImpl));
});

test('history always reads nuget.org and refuses to run without the old feed', () => {
  const sources = historySources({
    GITHUB_REPOSITORY_OWNER: 'Owner', GITHUB_ACTOR: 'actor', GITHUB_TOKEN: 'token',
  });
  assert.deepEqual(sources.map(s => s.source), [NUGET_ORG, 'https://nuget.pkg.github.com/Owner/index.json']);
  assert.deepEqual(sources[0].headers, {});
  assert.equal(sources[1].headers.authorization, `Basic ${Buffer.from('actor:token').toString('base64')}`);
  assert.throws(() => historySources({ GITHUB_REPOSITORY_OWNER: 'Owner', GITHUB_ACTOR: 'actor' }));
});
