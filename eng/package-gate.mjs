// J19 package handoff gate: pure version-compatibility rules for the local-feed consumer check.
// eng/test-package-consumer.ps1 collects the inputs (assets files, staged nuspecs, pins) and calls
// the CLI below; eng/package-gate.test.mjs exercises the rules with fixtures.
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Each provider package consumes exactly one upstream engine family, all at one version.
export const families = [
  { name: 'Broiler.JavaScript', prefix: 'Broiler.JavaScript.', provider: 'Broiler.JSeal.BroilerJs' },
  { name: 'Broiler.VM', prefix: 'Broiler.VM.', provider: 'Broiler.JSeal.Vm' },
];

// NuGet version grammar: one to four numeric parts, an optional dotted prerelease label and optional
// build metadata. NuGet treats a missing part as 0 ('8.0' is 8.0.0) and ignores metadata when comparing.
const versionPattern =
  /^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;
const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
const same = (a, b) => a.toLowerCase() === b.toLowerCase();

/** Parses one NuGet version; returns null for anything else (floating versions, ranges, blanks). */
export function parseVersion(text) {
  const match = typeof text === 'string' ? versionPattern.exec(text) : null;
  if (!match) return null;
  const [major, minor, patch, revision] = match.slice(1, 5).map(part => Number(part ?? 0));
  const release = match[5] ?? '';
  return { major, minor, patch, revision, release, metadata: match[6] ?? '',
    // NuGet's normalized form: three parts, a fourth only when non-zero, no metadata.
    normalized: `${major}.${minor}.${patch}${revision ? `.${revision}` : ''}${release ? `-${release}` : ''}` };
}

function comparePrerelease(a, b) {
  // A release sorts above every prerelease of the same numbers.
  if (!a || !b) return a ? -1 : b ? 1 : 0;
  const left = a.split('.');
  const right = b.split('.');
  for (let i = 0; i < Math.min(left.length, right.length); i++) {
    const [x, y] = [left[i], right[i]];
    const [xn, yn] = [/^\d+$/.test(x), /^\d+$/.test(y)];
    let order;
    if (xn && yn) order = Math.sign(Number(x) - Number(y));
    else if (xn !== yn) order = xn ? -1 : 1;
    // NuGet compares labels case-insensitively: 1.0.0-Preview.1 and 1.0.0-preview.1 are one version.
    else order = ordinal(x.toLowerCase(), y.toLowerCase());
    if (order) return order;
  }
  return Math.sign(left.length - right.length);
}

/** NuGet's version order (SemVer 2.0 precedence, four numeric parts, case-insensitive labels). */
export function compareVersions(a, b) {
  const [x, y] = [a, b].map(value => (typeof value === 'string' ? parseVersion(value) : value));
  if (!x || !y) throw new Error(`Cannot compare '${a}' and '${b}' as NuGet versions.`);
  for (const part of ['major', 'minor', 'patch', 'revision']) {
    if (x[part] !== y[part]) return Math.sign(x[part] - y[part]);
  }
  return comparePrerelease(x.release, y.release);
}

export const sameVersion = (a, b) => compareVersions(a, b) === 0;

/**
 * Returns the normalized form of an exact NuGet version (two to four parts accepted);
 * refuses floating versions, ranges and build metadata.
 */
export function parseExactVersion(version) {
  const parsed = parseVersion(version);
  if (!parsed || parsed.metadata) {
    throw new Error(`Expected an exact version such as 0.1.0-preview.3, not '${version}'.`);
  }
  return parsed.normalized;
}

/**
 * Parses a declared NuGet dependency range: a bare version (a floor), '[a]', '[a, b)', '(a, )',
 * '(, b]' and so on. Floating ranges ('1.*', '1.0.0-*') and a missing range are refused, because what
 * they resolve to depends on what a feed happens to contain.
 */
export function parseRange(range) {
  if (range === undefined || range === null || String(range).trim() === '') {
    throw new Error('A dependency without a version range resolves to whatever the feed offers.');
  }
  const text = String(range).trim();
  if (text.includes('*')) throw new Error(`Dependency range '${text}' floats.`);
  const bare = parseVersion(text);
  if (bare) return { min: bare, minInclusive: true, max: null, maxInclusive: false, text };
  const match = /^([[(])\s*([^,\])]*?)\s*(?:,\s*([^,\])]*?)\s*)?([\])])$/.exec(text);
  if (!match) throw new Error(`Dependency range '${text}' is not a NuGet version range.`);
  const [, open, low, high, close] = match;
  const hasComma = text.includes(',');
  const min = low ? parseVersion(low) : null;
  const max = hasComma ? (high ? parseVersion(high) : null) : min;
  if ((low && !min) || (hasComma && high && !max)) throw new Error(`Dependency range '${text}' has an invalid version.`);
  if (!hasComma && (open !== '[' || close !== ']' || !min)) throw new Error(`Dependency range '${text}' is not a NuGet version range.`);
  return { min, minInclusive: open === '[', max, maxInclusive: close === ']', text };
}

/** Whether a version satisfies a parsed range. */
export function satisfies(range, version) {
  if (range.min) {
    const order = compareVersions(version, range.min);
    if (order < 0 || (order === 0 && !range.minInclusive)) return false;
  }
  if (range.max) {
    const order = compareVersions(version, range.max);
    if (order > 0 || (order === 0 && !range.maxInclusive)) return false;
  }
  return true;
}

/** The inclusive lower bound of a declared dependency range, normalized: NuGet's resolution floor. */
export function lowerBound(range) {
  const parsed = parseRange(range);
  if (!parsed.min || !parsed.minInclusive) {
    throw new Error(`Dependency range '${parsed.text}' has no exact inclusive lower bound.`);
  }
  if (parsed.min.metadata) throw new Error(`Dependency range '${parsed.text}' carries build metadata.`);
  return parsed.min.normalized;
}

const maxVersion = versions => versions.reduce((a, b) => (compareVersions(a, b) >= 0 ? a : b));
const sortVersions = versions => [...versions].sort(compareVersions);
/** Distinct versions under NuGet equality (so '1.0.0-Preview.1' and '1.0.0-preview.1' are one). */
function distinctVersions(versions) {
  const result = [];
  for (const version of versions) if (!result.some(other => sameVersion(other, version))) result.push(version);
  return sortVersions(result);
}

export function familyOf(id) {
  return families.find(family => id.toLowerCase().startsWith(family.prefix.toLowerCase()));
}

/** Central package pins from Directory.Packages.props text, as id -> normalized exact version. */
export function readPins(propsText) {
  const pins = new Map();
  for (const [element] of propsText.matchAll(/<PackageVersion\b[^>]*>/g)) {
    const id = /\bInclude="([^"]+)"/.exec(element)?.[1];
    const version = /\bVersion="([^"]*)"/.exec(element)?.[1];
    if (!id) throw new Error(`PackageVersion without Include: ${element}`);
    if ([...pins.keys()].some(existing => same(existing, id))) throw new Error(`Duplicate pin for ${id}.`);
    try { pins.set(id, parseExactVersion(version)); }
    catch (error) { throw new Error(`${id}: ${error.message}`); }
  }
  return pins;
}

/** One version per engine family; a family pinned at several versions is refused. */
export function familyPins(pins) {
  const result = {};
  for (const family of families) {
    const versions = distinctVersions([...pins].filter(([id]) => familyOf(id) === family).map(([, v]) => v));
    if (versions.length > 1) throw new Error(`${family.name} pins mix ${versions.join(', ')}.`);
    if (versions.length) result[family.name] = versions[0];
  }
  return result;
}

/** Parses Family=version arguments. Only whole families can be candidates. */
export function parseCandidates(list) {
  if (!Array.isArray(list) || !list.length) throw new Error('Name at least one Family=version candidate.');
  const result = {};
  for (const item of list) {
    const match = /^([^=]+)=(.*)$/.exec(String(item));
    const family = match && families.find(f => f.name === match[1]);
    if (!family) {
      throw new Error(`Candidate '${item}' must be ${families.map(f => `${f.name}=<version>`).join(' or ')}.`);
    }
    if (family.name in result) throw new Error(`Candidate ${family.name} is named twice.`);
    result[family.name] = parseExactVersion(match[2]);
  }
  return result;
}

/** Rewrites every pin of each candidate family, leaving all other text unchanged. */
export function applyCandidates(propsText, candidates) {
  const current = familyPins(readPins(propsText));
  let text = propsText;
  for (const [name, version] of Object.entries(candidates)) {
    const family = families.find(f => f.name === name);
    if (!family || !current[name]) throw new Error(`Candidate ${name} has no pins to replace.`);
    if (sameVersion(current[name], parseExactVersion(version))) {
      throw new Error(`Candidate ${name} ${version} is already pinned; a candidate must be a new version.`);
    }
    text = text.replace(/<PackageVersion\b[^>]*>/g, element => {
      const id = /\bInclude="([^"]+)"/.exec(element)[1];
      return familyOf(id) === family ? element.replace(/\bVersion="[^"]*"/, `Version="${version}"`) : element;
    });
  }
  return text;
}

function graphEntries(assets) {
  const targets = Object.entries(assets.targets ?? {});
  if (targets.length !== 1) throw new Error('Expected exactly one target framework in project.assets.json.');
  const [framework, target] = targets[0];
  const entries = Object.entries(assets.libraries ?? {}).map(([key, library]) => {
    const [id, version] = key.split('/');
    return { key, id, version, type: library.type, sha512: library.sha512,
      dependencies: Object.entries(target[key]?.dependencies ?? {}) };
  }).sort((a, b) => ordinal(a.id, b.id) || compareVersions(a.version, b.version));
  // The restored project's own package references take part in unification like any other declaration.
  const frameworks = assets.project?.frameworks ?? {};
  const own = frameworks[framework] ?? Object.values(frameworks)[0] ?? {};
  const direct = Object.entries(own.dependencies ?? {})
    .filter(([, dependency]) => (dependency.target ?? 'Package') === 'Package')
    .map(([id, dependency]) => [id, dependency.version]);
  return { entries, direct };
}

/**
 * Checks one graph's declared ranges against what resolved. NuGet resolves each id at the lowest
 * version every declaration of it allows, which is the highest declared lower bound across the graph
 * (unification). A higher resolved version is a floating or opportunistic upgrade; one outside a
 * declared range is a downgrade or a conflict. Both are refused.
 */
function checkResolution(graphName, entries, direct, errors) {
  const declared = new Map();
  const declarers = [{ key: 'project', dependencies: direct }, ...entries.filter(entry => entry.type === 'package')];
  for (const declarer of declarers) {
    for (const [dependencyId, range] of declarer.dependencies) {
      const resolved = entries.find(candidate => same(candidate.id, dependencyId));
      if (!resolved) {
        errors.push(`${graphName}: ${declarer.key} depends on missing ${dependencyId} ${range ?? '(no range)'}.`);
        continue;
      }
      let parsed;
      let bound;
      try { parsed = parseRange(range); bound = lowerBound(range); }
      catch (error) { errors.push(`${graphName}: ${declarer.key}: ${dependencyId}: ${error.message}`); continue; }
      const list = declared.get(resolved.key) ?? [];
      list.push({ by: declarer.key, range: String(range).trim(), parsed, bound });
      declared.set(resolved.key, list);
    }
  }
  for (const [key, declarations] of [...declared].sort(([a], [b]) => ordinal(a, b))) {
    const resolved = entries.find(entry => entry.key === key);
    const floor = maxVersion(declarations.map(declaration => declaration.bound));
    if (!sameVersion(resolved.version, floor)) {
      const floors = declarations.map(declaration => `${declaration.by} ${declaration.bound}`).join(', ');
      errors.push(`${graphName}: ${resolved.id} resolved ${resolved.version}, not the highest declared lower bound ${floor} (${floors}).`);
    }
    for (const declaration of declarations.filter(declaration => !satisfies(declaration.parsed, resolved.version))) {
      errors.push(`${graphName}: ${declaration.by} declares ${resolved.id} ${declaration.range}, which ${resolved.version} does not satisfy.`);
    }
  }
}

/**
 * Evaluates the resolved graphs and staged feed of one gate run. Returns a report whose
 * `errors` list is empty only when every rule holds; every resolved identity is recorded.
 */
export function evaluateGate({ packagesProps, candidates = {}, candidateFeed = [], graphs, feed }) {
  const errors = [];
  const expected = familyPins(readPins(packagesProps));
  for (const [name, version] of Object.entries(candidates)) {
    if (!expected[name] || !sameVersion(expected[name], parseExactVersion(version))) {
      errors.push(`candidate ${name} ${version} is not what the build pins (${expected[name] ?? 'none'}).`);
    }
  }
  const report = { schemaVersion: 1, expected, candidates, graphs: {}, feed: [], errors };
  const seenFamilies = {};

  for (const graph of graphs) {
    const { entries, direct } = graphEntries(graph.assets);
    const packages = entries.filter(entry => entry.type === 'package');
    const projects = entries.filter(entry => entry.type !== 'package');
    const summary = { kind: graph.kind, packages: packages.map(e => e.key), projects: projects.map(e => e.key), families: {} };
    report.graphs[graph.name] = summary;
    if (graph.kind === 'consumer') {
      for (const project of projects) errors.push(`${graph.name}: project reference ${project.key} in a package consumer.`);
    }

    for (const family of families) {
      const members = packages.filter(entry => familyOf(entry.id) === family);
      const versions = distinctVersions(members.map(entry => entry.version));
      const hasProvider = entries.some(entry => same(entry.id, family.provider));
      if (hasProvider && !versions.length) errors.push(`${graph.name}: ${family.provider} is present without the ${family.name} family.`);
      if (!versions.length) continue;
      if (versions.length > 1) {
        errors.push(`${graph.name}: ${family.name} family mixes ${versions.join(', ')}.`);
        continue;
      }
      summary.families[family.name] = versions[0];
      (seenFamilies[family.name] ??= []).push(versions[0]);
      if (!expected[family.name] || !sameVersion(versions[0], expected[family.name])) {
        errors.push(`${graph.name}: ${family.name} resolved ${versions[0]} but ${expected[family.name] ?? 'no pinned version'} is expected.`);
      }
      if (family.name in candidates) {
        for (const member of members) {
          const staged = candidateFeed.find(c => same(c.id, member.id) && sameVersion(c.version, member.version));
          if (!staged) errors.push(`${graph.name}: ${member.key} is not in the candidate feed.`);
          else if (staged.sha512 !== member.sha512) errors.push(`${graph.name}: ${member.key} archive differs from the candidate feed.`);
        }
      }
    }

    checkResolution(graph.name, entries, direct, errors);
  }

  for (const [name, versions] of Object.entries(seenFamilies)) {
    const distinct = distinctVersions(versions);
    if (distinct.length > 1) errors.push(`${name} resolves to different versions across graphs: ${distinct.join(', ')}.`);
  }

  // The staged feed must be closed: every declared dependency is staged at a version inside its range
  // that is itself a declared lower bound for that id - its own, or a higher one another staged package
  // declares, which is what unification picks. Nothing is staged only as an upgrade.
  const floors = new Map();
  for (const entry of feed) {
    for (const dependency of entry.dependencies ?? []) {
      let bound;
      try { bound = lowerBound(dependency.range); } catch { continue; } // reported below
      const key = dependency.id.toLowerCase();
      floors.set(key, [...(floors.get(key) ?? []), bound]);
    }
  }
  for (const entry of [...feed].sort((a, b) => ordinal(a.id, b.id) || compareVersions(a.version, b.version))) {
    report.feed.push(`${entry.id}/${entry.version}`);
    const providerOf = families.find(family => same(family.provider, entry.id));
    for (const dependency of entry.dependencies ?? []) {
      let bound;
      let parsed;
      try { parsed = parseRange(dependency.range); bound = lowerBound(dependency.range); }
      catch (error) { errors.push(`feed: ${entry.id}/${entry.version}: ${dependency.id}: ${error.message}`); continue; }
      const allowed = floors.get(dependency.id.toLowerCase()) ?? [];
      const staged = feed.some(other => same(other.id, dependency.id) && satisfies(parsed, other.version)
        && allowed.some(floor => sameVersion(floor, other.version)));
      if (!staged) {
        errors.push(`feed: ${entry.id}/${entry.version} depends on ${dependency.id} ${bound}, which is not staged.`);
      }
      const dependencyFamily = familyOf(dependency.id);
      if (!providerOf || !dependencyFamily) continue;
      if (dependencyFamily !== providerOf) {
        errors.push(`feed: ${entry.id}/${entry.version} must not depend on the ${dependencyFamily.name} family (${dependency.id}).`);
      } else if (!expected[providerOf.name] || !sameVersion(bound, expected[providerOf.name])) {
        errors.push(`feed: ${entry.id}/${entry.version} declares ${dependency.id} ${bound}; the gated ${providerOf.name} version is ${expected[providerOf.name]}.`);
      }
    }
  }
  return report;
}

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8').replace(/^﻿/, ''));
}

// CLI used by eng/test-package-consumer.ps1:
//   pins <Directory.Packages.props>                         -> family pins as JSON
//   candidate-props <in.props> <out.props> Family=version...  -> rewrite a staged copy
//   check <request.json> <report.json>                      -> exit 1 when any rule fails
function main([command, ...args]) {
  if (command === 'pins' && args.length === 1) {
    console.log(JSON.stringify(familyPins(readPins(readFileSync(args[0], 'utf8')))));
  } else if (command === 'candidate-props' && args.length >= 3) {
    const candidates = parseCandidates(args.slice(2));
    writeFileSync(args[1], applyCandidates(readFileSync(args[0], 'utf8'), candidates));
    console.log(JSON.stringify({ candidates, patterns: Object.keys(candidates).map(name =>
      `${families.find(f => f.name === name).prefix}*`) }));
  } else if (command === 'check' && args.length === 2) {
    const request = readJson(args[0]);
    const report = evaluateGate({
      packagesProps: readFileSync(request.packagesPropsPath, 'utf8'),
      candidates: request.candidates ?? {},
      candidateFeed: request.candidateFeed ?? [],
      feed: request.feed,
      graphs: request.graphs.map(graph => ({ name: graph.name, kind: graph.kind, assets: readJson(graph.assetsPath) })),
    });
    writeFileSync(args[1], `${JSON.stringify(report, null, 2)}\n`);
    for (const error of report.errors) console.error(error);
    console.log(`J19 gate: ${Object.keys(report.graphs).length} graphs, ${report.feed.length} staged packages, ${report.errors.length} errors.`);
    if (report.errors.length) process.exitCode = 1;
  } else {
    throw new Error('Usage: package-gate.mjs pins <props> | candidate-props <in> <out> Family=version... | check <request> <report>');
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(process.argv.slice(2)); }
  catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
