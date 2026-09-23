import assert from 'node:assert/strict';
import test from 'node:test';
import {
  applyCandidates, compareVersions, evaluateGate, familyPins, lowerBound, parseCandidates, parseExactVersion, readPins,
} from './package-gate.mjs';

const props = `<Project>
  <ItemGroup>
    <PackageVersion Include="Broiler.JavaScript.BuiltIns" Version="0.1.0-preview.1" />
    <PackageVersion Include="Broiler.JavaScript.Globals" Version="0.1.0-preview.1" />
    <PackageVersion Include="Broiler.VM.Runtime" Version="0.1.0-preview.3" />
    <PackageVersion Include="Broiler.VM.Profile.JavaScript" Version="0.1.0-preview.3" />
    <PackageVersion Include="xunit" Version="2.5.3" />
  </ItemGroup>
</Project>`;

// A minimal project.assets.json: libraries carry type/sha512, targets carry declared ranges.
function assets(packages, projects = []) {
  const libraries = {};
  const target = {};
  for (const [key, dependencies = {}, sha512 = `hash:${key}`] of packages) {
    libraries[key] = { type: 'package', sha512 };
    target[key] = { type: 'package', dependencies };
  }
  for (const key of projects) {
    libraries[key] = { type: 'project' };
    target[key] = { type: 'project' };
  }
  return { libraries, targets: { 'net10.0': target } };
}

const vm3 = [
  ['Broiler.VM.Abstractions/0.1.0-preview.3'],
  ['Broiler.VM.Runtime/0.1.0-preview.3', { 'Broiler.VM.Abstractions': '0.1.0-preview.3' }],
  ['Broiler.VM.Profile.JavaScript/0.1.0-preview.3', { 'Broiler.VM.Abstractions': '0.1.0-preview.3' }],
];
const js1 = [
  ['Broiler.JavaScript.Runtime/0.1.0-preview.1', { 'Broiler.Regex': '0.1.0-preview.1' }],
  ['Broiler.JavaScript.BuiltIns/0.1.0-preview.1', { 'Broiler.JavaScript.Runtime': '0.1.0-preview.1' }],
  ['Broiler.Regex/0.1.0-preview.1'],
];
const vmProvider = ['Broiler.JSeal.Vm/0.2.0-preview.1',
  { 'Broiler.JSeal': '0.2.0-preview.1', 'Broiler.VM.Runtime': '0.1.0-preview.3', 'Broiler.VM.Profile.JavaScript': '0.1.0-preview.3' }];
const jsProvider = ['Broiler.JSeal.BroilerJs/0.2.0-preview.1',
  { 'Broiler.JSeal': '0.2.0-preview.1', 'Broiler.JavaScript.BuiltIns': '0.1.0-preview.1' }];
const contracts = ['Broiler.JSeal/0.2.0-preview.1'];

function feedFrom(...lists) {
  const seen = new Map();
  for (const [key, dependencies = {}, sha512 = `hash:${key}`] of lists.flat()) {
    const [id, version] = key.split('/');
    seen.set(key, { id, version, sha512,
      dependencies: Object.entries(dependencies).map(([depId, range]) => ({ id: depId, range })) });
  }
  return [...seen.values()];
}

function passingRequest() {
  return {
    packagesProps: props,
    graphs: [
      { name: 'source-Vm', kind: 'source', assets: assets(vm3, ['Broiler.JSeal/0.2.0-preview.1']) },
      { name: 'source-BroilerJs', kind: 'source', assets: assets(js1, ['Broiler.JSeal/0.2.0-preview.1']) },
      { name: 'consumer-Vm', kind: 'consumer', assets: assets([...vm3, vmProvider, contracts]) },
      { name: 'consumer-BroilerJs', kind: 'consumer', assets: assets([...js1, jsProvider, contracts]) },
      { name: 'consumer-Both', kind: 'consumer', assets: assets([...vm3, ...js1, vmProvider, jsProvider, contracts]) },
    ],
    feed: feedFrom(vm3, js1, [vmProvider, jsProvider, contracts]),
  };
}

test('only exact versions are accepted; floating, ranges and metadata are refused', () => {
  for (const version of ['0.1.0', '0.1.0-preview.3', '10.20.30-rc.1.2', '1.2.3.4']) {
    assert.equal(parseExactVersion(version), version);
  }
  for (const version of ['*', '0.1.*', '0.1.0-*', '[0.1.0,)', '[0.1.0]', '(,1.0]', '0.1.0+sha', '0.1.0.0.0',
    ' 0.1.0', '0.1.0-', '0.1.0-preview.3;x', '', undefined]) {
    assert.throws(() => parseExactVersion(version), `${version} must be refused`);
  }
});

test('declared lower bounds are exact; open-ended floors and floating ranges are refused', () => {
  assert.equal(lowerBound('0.1.0-preview.3'), '0.1.0-preview.3');
  assert.equal(lowerBound('[0.1.0-preview.3]'), '0.1.0-preview.3');
  assert.equal(lowerBound('[0.1.0-preview.3, )'), '0.1.0-preview.3');
  assert.equal(lowerBound('[0.1.0-preview.3,0.2.0)'), '0.1.0-preview.3');
  for (const range of ['*', '0.1.*', '(0.1.0,)', '(,0.2.0]', '[,0.2.0)', '[0.1.0)', '[0.1.0-preview.*]',
    '(,)', '[0.1.0,0.2.0', '[0.1.0, x)', '', undefined, null]) {
    assert.throws(() => lowerBound(range), `${range} must be refused`);
  }
});

test('default pins are read per family and a family split across versions is refused', () => {
  const pins = readPins(props);
  assert.equal(pins.get('Broiler.VM.Runtime'), '0.1.0-preview.3');
  assert.equal(pins.get('xunit'), '2.5.3');
  assert.deepEqual(familyPins(pins), { 'Broiler.JavaScript': '0.1.0-preview.1', 'Broiler.VM': '0.1.0-preview.3' });
  const split = props.replace('"Broiler.VM.Runtime" Version="0.1.0-preview.3"', '"Broiler.VM.Runtime" Version="0.1.0-preview.4"');
  assert.throws(() => familyPins(readPins(split)), /Broiler\.VM.*0\.1\.0-preview\.3.*0\.1\.0-preview\.4/);
  assert.throws(() => readPins(props.replace('2.5.3', '2.5.*')), /xunit/);
  assert.throws(() => readPins(`${props}<PackageVersion Include="xunit" Version="2.5.3" />`), /Duplicate/);
});

test('candidate arguments name a known family once with an exact version', () => {
  assert.deepEqual(parseCandidates(['Broiler.VM=0.1.0-preview.4']), { 'Broiler.VM': '0.1.0-preview.4' });
  assert.deepEqual(parseCandidates(['Broiler.VM=0.1.0-preview.4', 'Broiler.JavaScript=0.1.0-preview.2']),
    { 'Broiler.VM': '0.1.0-preview.4', 'Broiler.JavaScript': '0.1.0-preview.2' });
  for (const list of [[], ['Broiler.VM'], ['Broiler.VM=*'], ['Broiler.VM=0.1.0-preview.*'],
    ['Broiler.Regex=0.1.0'], ['Broiler.VM.Runtime=0.1.0-preview.4'], ['Broiler.VM=1.0.0', 'Broiler.VM=1.0.1']]) {
    assert.throws(() => parseCandidates(list), JSON.stringify(list));
  }
});

test('a candidate rewrites every pin of its family and nothing else', () => {
  const rewritten = applyCandidates(props, { 'Broiler.VM': '0.1.0-preview.4' });
  const pins = readPins(rewritten);
  assert.equal(pins.get('Broiler.VM.Runtime'), '0.1.0-preview.4');
  assert.equal(pins.get('Broiler.VM.Profile.JavaScript'), '0.1.0-preview.4');
  assert.equal(pins.get('Broiler.JavaScript.BuiltIns'), '0.1.0-preview.1');
  assert.equal(pins.get('xunit'), '2.5.3');
  assert.equal(rewritten.replaceAll('0.1.0-preview.4', '0.1.0-preview.3'), props);
  // Re-using the pinned version would let a locally built archive shadow the published identity.
  assert.throws(() => applyCandidates(props, { 'Broiler.VM': '0.1.0-preview.3' }), /already pinned/);
  assert.throws(() => applyCandidates('<Project />', { 'Broiler.VM': '0.1.0-preview.4' }), /no pins/);
});

test('the default pinned package set passes and every resolved identity is recorded', () => {
  const report = evaluateGate(passingRequest());
  assert.deepEqual(report.errors, []);
  assert.deepEqual(report.expected, { 'Broiler.JavaScript': '0.1.0-preview.1', 'Broiler.VM': '0.1.0-preview.3' });
  assert.deepEqual(report.graphs['consumer-Both'].packages, [
    'Broiler.JSeal/0.2.0-preview.1', 'Broiler.JSeal.BroilerJs/0.2.0-preview.1', 'Broiler.JSeal.Vm/0.2.0-preview.1',
    'Broiler.JavaScript.BuiltIns/0.1.0-preview.1', 'Broiler.JavaScript.Runtime/0.1.0-preview.1', 'Broiler.Regex/0.1.0-preview.1',
    'Broiler.VM.Abstractions/0.1.0-preview.3', 'Broiler.VM.Profile.JavaScript/0.1.0-preview.3', 'Broiler.VM.Runtime/0.1.0-preview.3',
  ]);
  assert.deepEqual(report.graphs['consumer-Both'].families, { 'Broiler.JavaScript': '0.1.0-preview.1', 'Broiler.VM': '0.1.0-preview.3' });
  assert.deepEqual(report.graphs['source-Vm'].projects, ['Broiler.JSeal/0.2.0-preview.1']);
});

test('a mixed family inside one graph is rejected', () => {
  const request = passingRequest();
  const mixed = vm3.map(([key, deps]) => [key.replace('Runtime/0.1.0-preview.3', 'Runtime/0.1.0-preview.4'), deps]);
  request.graphs[2].assets = assets([...mixed, vmProvider, contracts]);
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /consumer-Vm: Broiler\.VM family mixes 0\.1\.0-preview\.3, 0\.1\.0-preview\.4/.test(e)), errors.join('\n'));
});

test('a family that differs from the pins, or between graphs, is rejected', () => {
  const request = passingRequest();
  const upgraded = vm3.map(([key, deps]) => [key.replace('preview.3', 'preview.4'),
    Object.fromEntries(Object.entries(deps ?? {}).map(([id, range]) => [id, range.replace('preview.3', 'preview.4')]))]);
  request.graphs[0].assets = assets(upgraded, ['Broiler.JSeal/0.2.0-preview.1']);
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /source-Vm: Broiler\.VM resolved 0\.1\.0-preview\.4 but 0\.1\.0-preview\.3 is expected/.test(e)), errors.join('\n'));
  assert.ok(errors.some(e => /Broiler\.VM resolves to different versions across graphs/.test(e)), errors.join('\n'));
});

test('a provider package must declare exactly the family version it is gated with', () => {
  const request = passingRequest();
  const stale = ['Broiler.JSeal.Vm/0.2.0-preview.1',
    { 'Broiler.JSeal': '0.2.0-preview.1', 'Broiler.VM.Runtime': '0.1.0-preview.2', 'Broiler.VM.Profile.JavaScript': '0.1.0-preview.3' }];
  request.feed = feedFrom(vm3, js1, [stale, jsProvider, contracts]);
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /Broiler\.JSeal\.Vm\/0\.2\.0-preview\.1 declares Broiler\.VM\.Runtime 0\.1\.0-preview\.2; the gated Broiler\.VM version is 0\.1\.0-preview\.3/.test(e)), errors.join('\n'));
  const cross = ['Broiler.JSeal.Vm/0.2.0-preview.1', { 'Broiler.JSeal': '0.2.0-preview.1', 'Broiler.JavaScript.Runtime': '0.1.0-preview.1' }];
  request.feed = feedFrom(vm3, js1, [cross, jsProvider, contracts]);
  assert.ok(evaluateGate(request).errors.some(e => /Broiler\.JSeal\.Vm.*must not depend on the Broiler\.JavaScript family/.test(e)));
});

test('a provider without its engine family is rejected', () => {
  const request = passingRequest();
  request.graphs[2].assets = assets([vmProvider, contracts]);
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /consumer-Vm: Broiler\.JSeal\.Vm is present without the Broiler\.VM family/.test(e)), errors.join('\n'));
});

test('a dependency resolved above every declared lower bound is an opportunistic upgrade', () => {
  const request = passingRequest();
  const floated = [['Broiler.JavaScript.Runtime/0.1.0-preview.1', { 'Broiler.Regex': '0.1.0-preview.1' }],
    ['Broiler.JavaScript.BuiltIns/0.1.0-preview.1', { 'Broiler.JavaScript.Runtime': '0.1.0-preview.1' }],
    ['Broiler.Regex/0.1.0-preview.2']];
  request.graphs[1].assets = assets(floated, ['Broiler.JSeal/0.2.0-preview.1']);
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /source-BroilerJs: Broiler\.Regex resolved 0\.1\.0-preview\.2, not the highest declared lower bound 0\.1\.0-preview\.1 \(Broiler\.JavaScript\.Runtime\/0\.1\.0-preview\.1 0\.1\.0-preview\.1\)/.test(e)), errors.join('\n'));
});

test('missing dependencies fail: absent from a graph, or absent from the staged feed', () => {
  const request = passingRequest();
  request.graphs[3].assets = assets([...js1.filter(([key]) => !key.startsWith('Broiler.Regex')), jsProvider, contracts]);
  request.feed = request.feed.filter(entry => entry.id !== 'Broiler.Regex');
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /consumer-BroilerJs: Broiler\.JavaScript\.Runtime\/0\.1\.0-preview\.1 depends on missing Broiler\.Regex/.test(e)), errors.join('\n'));
  assert.ok(errors.some(e => /feed: Broiler\.JavaScript\.Runtime\/0\.1\.0-preview\.1 depends on Broiler\.Regex 0\.1\.0-preview\.1, which is not staged/.test(e)), errors.join('\n'));
});

test('a consumer graph containing a project reference is rejected', () => {
  const request = passingRequest();
  request.graphs[2].assets = assets([...vm3, vmProvider], ['Broiler.JSeal/0.2.0-preview.1']);
  assert.ok(evaluateGate(request).errors.some(e => /consumer-Vm: project reference Broiler\.JSeal\/0\.2\.0-preview\.1/.test(e)));
});

test('candidate families must resolve from the candidate feed with identical archives', () => {
  const vm4 = vm3.map(([key, deps]) => [key.replace('preview.3', 'preview.4'),
    Object.fromEntries(Object.entries(deps ?? {}).map(([id, range]) => [id, range.replace('preview.3', 'preview.4')]))]);
  const vmProvider4 = [vmProvider[0], Object.fromEntries(Object.entries(vmProvider[1]).map(([id, range]) =>
    [id, range.replace('preview.3', 'preview.4')]))];
  const request = {
    packagesProps: applyCandidates(props, { 'Broiler.VM': '0.1.0-preview.4' }),
    candidates: { 'Broiler.VM': '0.1.0-preview.4' },
    candidateFeed: feedFrom(vm4),
    graphs: [
      { name: 'source-Vm', kind: 'source', assets: assets(vm4, ['Broiler.JSeal/0.2.0-preview.1']) },
      { name: 'consumer-Both', kind: 'consumer', assets: assets([...vm4, ...js1, vmProvider4, jsProvider, contracts]) },
    ],
    feed: feedFrom(vm4, js1, [vmProvider4, jsProvider, contracts]),
  };
  assert.deepEqual(evaluateGate(request).errors, []);
  assert.deepEqual(evaluateGate(request).expected, { 'Broiler.JavaScript': '0.1.0-preview.1', 'Broiler.VM': '0.1.0-preview.4' });

  const tampered = structuredClone(request);
  tampered.candidateFeed[0].sha512 = 'other';
  assert.ok(evaluateGate(tampered).errors.some(e => /Broiler\.VM\.Abstractions\/0\.1\.0-preview\.4 .*differs from the candidate feed/.test(e)));
  const absent = structuredClone(request);
  absent.candidateFeed = absent.candidateFeed.filter(entry => entry.id !== 'Broiler.VM.Runtime');
  assert.ok(evaluateGate(absent).errors.some(e => /Broiler\.VM\.Runtime\/0\.1\.0-preview\.4 .*not in the candidate feed/.test(e)));
  // The staged props must actually carry the candidate: a mismatch means the build was not the candidate.
  const unapplied = structuredClone(request);
  unapplied.packagesProps = props;
  assert.ok(evaluateGate(unapplied).errors.some(e => /candidate Broiler\.VM 0\.1\.0-preview\.4 is not what the build pins \(0\.1\.0-preview\.3\)/.test(e)));
});

test('NuGet version forms: two and four parts, normalization and precedence', () => {
  assert.equal(parseExactVersion('8.0'), '8.0.0');
  assert.equal(parseExactVersion('8'), '8.0.0');
  assert.equal(parseExactVersion('8.0.0.0'), '8.0.0');
  assert.equal(parseExactVersion('1.2.3.4-beta'), '1.2.3.4-beta');
  assert.equal(compareVersions('8.0', '8.0.0.0'), 0);
  assert.equal(compareVersions('1.2.3', '1.2.3.1'), -1);
  // Prerelease labels compare case-insensitively, numeric identifiers numerically, and a release
  // sorts above its prereleases.
  assert.equal(compareVersions('0.1.0-Preview.3', '0.1.0-preview.3'), 0);
  assert.equal(compareVersions('0.1.0-preview.10', '0.1.0-preview.9'), 1);
  assert.equal(compareVersions('0.1.0-preview.4', '0.1.0-preview.4-local.jseal.1'), -1);
  // '4-local' is alphanumeric and so sorts above every numeric identifier (see the README).
  assert.equal(compareVersions('0.1.0-preview.5', '0.1.0-preview.4-local.jseal.1'), -1);
  assert.equal(compareVersions('0.1.0-preview.5', '0.1.0-preview.4.local.1'), 1);
  assert.equal(compareVersions('0.1.0-preview.3', '0.1.0'), -1);
  assert.equal(compareVersions('1.0.0-alpha.1', '1.0.0-alpha.beta'), -1);
  assert.equal(compareVersions('1.0.0-alpha', '1.0.0-alpha.1'), -1);
  assert.equal(compareVersions('1.0.0+build', '1.0.0'), 0);
});

test('declared ranges in every NuGet form yield their inclusive lower bound', () => {
  assert.equal(lowerBound('[8.0, )'), '8.0.0');
  assert.equal(lowerBound('8.0'), '8.0.0');
  assert.equal(lowerBound('[1.0.0.0]'), '1.0.0');
  assert.equal(lowerBound('[1.2.3.4, 2.0)'), '1.2.3.4');
  assert.equal(lowerBound('[0.1.0-Preview.3, )'), '0.1.0-Preview.3');
});

test('pins, resolved versions and staged versions are equal under NuGet version equality', () => {
  const request = passingRequest();
  request.packagesProps = props.replaceAll('0.1.0-preview.3', '0.1.0-Preview.3');
  assert.deepEqual(evaluateGate(request).errors, []);
  const twoPart = passingRequest();
  twoPart.packagesProps = props.replace('Version="2.5.3"', 'Version="2.5"');
  assert.deepEqual(evaluateGate(twoPart).errors, []);
});

// Unification: X is declared at two floors in one graph, and NuGet resolves the higher one.
const unified = floor => [
  ['A/1.0.0', { X: '[1.0, )' }],
  ['B/1.0.0', { X: '[1.2.0, )' }],
  [`X/${floor}`],
];

test('a dependency resolves at the highest floor declared across the graph, and no higher', () => {
  const request = passingRequest();
  request.graphs = [{ name: 'unified', kind: 'consumer', assets: assets(unified('1.2.0')) }];
  request.feed = feedFrom(unified('1.2.0'));
  assert.deepEqual(evaluateGate(request).errors, []);

  const upgraded = structuredClone(request);
  upgraded.graphs[0].assets = assets(unified('1.3.0'));
  assert.ok(evaluateGate(upgraded).errors.some(e =>
    /unified: X resolved 1\.3\.0, not the highest declared lower bound 1\.2\.0 \(A\/1\.0\.0 1\.0\.0, B\/1\.0\.0 1\.2\.0\)/.test(e)));

  const downgraded = structuredClone(request);
  downgraded.graphs[0].assets = assets(unified('1.0.0'));
  const { errors } = evaluateGate(downgraded);
  assert.ok(errors.some(e => /unified: X resolved 1\.0\.0, not the highest declared lower bound 1\.2\.0/.test(e)), errors.join('\n'));
  assert.ok(errors.some(e => /unified: B\/1\.0\.0 declares X \[1\.2\.0, \), which 1\.0\.0 does not satisfy/.test(e)), errors.join('\n'));
});

test('the restoring project\'s own references take part in unification', () => {
  const graph = assets([['A/1.0.0', { X: '[1.0, )' }], ['X/1.5.0']]);
  graph.project = { frameworks: { 'net10.0': { dependencies: {
    X: { target: 'Package', version: '[1.5.0, )' }, A: { target: 'Package', version: '[1.0.0, )' } } } } };
  const request = passingRequest();
  request.graphs = [{ name: 'direct', kind: 'consumer', assets: graph }];
  request.feed = feedFrom([['A/1.0.0', { X: '[1.0, )' }], ['X/1.5.0']]);
  const { errors } = evaluateGate(request);
  assert.ok(!errors.some(e => e.startsWith('direct:')), errors.join('\n'));
  // Staged archives alone declare no 1.5.0 floor, so the feed is not closed without the project.
  assert.ok(errors.some(e => /feed: A\/1\.0\.0 depends on X 1\.0\.0, which is not staged/.test(e)), errors.join('\n'));

  const floated = structuredClone(request);
  floated.graphs[0].assets.libraries = { 'A/1.0.0': { type: 'package', sha512: 'a' }, 'X/1.6.0': { type: 'package', sha512: 'x' } };
  floated.graphs[0].assets.targets['net10.0'] = { 'A/1.0.0': { dependencies: { X: '[1.0, )' } }, 'X/1.6.0': {} };
  assert.ok(evaluateGate(floated).errors.some(e => /direct: X resolved 1\.6\.0, not the highest declared lower bound 1\.5\.0 \(project 1\.5\.0, A\/1\.0\.0 1\.0\.0\)/.test(e)));
});

test('the staged feed may carry a dependency at a higher declared floor, never at an undeclared one', () => {
  const request = passingRequest();
  request.graphs = [];
  request.feed = feedFrom(unified('1.2.0'));
  assert.deepEqual(evaluateGate(request).errors, []);
  request.feed = feedFrom(unified('1.3.0'));
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /feed: A\/1\.0\.0 depends on X 1\.0\.0, which is not staged/.test(e)), errors.join('\n'));
  assert.ok(errors.some(e => /feed: B\/1\.0\.0 depends on X 1\.2\.0, which is not staged/.test(e)), errors.join('\n'));
});

test('a dependency declared without a version is refused, not resolved at whatever the feed has', () => {
  const request = passingRequest();
  request.feed = [...request.feed, { id: 'Loose', version: '1.0.0', sha512: 'x', dependencies: [{ id: 'Broiler.Regex', range: null }] }];
  const { errors } = evaluateGate(request);
  assert.ok(errors.some(e => /feed: Loose\/1\.0\.0: Broiler\.Regex: A dependency without a version range/.test(e)), errors.join('\n'));
});
