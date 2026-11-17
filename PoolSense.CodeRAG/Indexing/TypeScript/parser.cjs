'use strict';

// Only this deployed compiler is loaded. Indexed package.json/tsconfig/plugins are never evaluated.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
let ts;
try {
  ts = require(path.join(__dirname, 'node_modules', 'typescript', 'lib', 'typescript.js'));
  if (ts.version !== '5.9.3') throw new Error(`Expected TypeScript 5.9.3, found ${ts.version}`);
} catch (error) {
  process.stderr.write(`Pinned TypeScript compiler unavailable; run npm ci in the deployed Indexing\\TypeScript directory. ${error.message}\n`);
  process.exit(1);
}

const schema = 1;
const own = (object, key) => Object.hasOwn(object, key) ? object[key] : undefined;
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const fileId = relative => `${relative}\0File`;
const declarationId = (relative, key) => `${relative}\0TypeScript:${key}`;

function checkLinks(target) {
  for (let current = target; ; current = path.dirname(current)) {
    try {
      if (fs.lstatSync(current).isSymbolicLink()) throw new Error(`Linked path rejected: ${current}`);
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
    }
    if (path.dirname(current) === current) break;
  }
}

function within(target, root) {
  const relative = path.relative(root, target);
  return relative === '' || (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`));
}

function outputPath(value, root) {
  const full = path.resolve(value);
  if (within(full, root)) throw new Error('Cache/output must be outside the indexed source root.');
  checkLinks(full);
  return full;
}

function parse(relative, text) {
  const extension = path.extname(relative).toLowerCase();
  const kind = { '.ts': ts.ScriptKind.TS, '.tsx': ts.ScriptKind.TSX, '.js': ts.ScriptKind.JS, '.jsx': ts.ScriptKind.JSX }[extension];
  const source = ts.createSourceFile(relative, text, ts.ScriptTarget.Latest, true, kind);
  const data = { declarations: [], scopes: [], imports: [], exports: Object.create(null), calls: [], classes: Object.create(null), objects: Object.create(null), invalidMembers: [], warnings: [] };
  const scope = (parent, functionScope = false) => {
    data.scopes.push({ parent, functionScope, bindings: Object.create(null) });
    return data.scopes.length - 1;
  };
  const top = scope(null, true);
  const modifiers = (node, flag) => !!(ts.getCombinedModifierFlags(node) & flag);
  const nameOf = node => node && (ts.isIdentifier(node) || ts.isPrivateIdentifier(node) || ts.isStringLiteralLike(node) || ts.isNumericLiteral(node)) ? node.text : null;
  const reference = (name, at) => ({ kind: 'name', name, scope: at });
  function typeExpression(node, at) {
    if (!node || !ts.isTypeReferenceNode(node)) return null;
    function typeName(name) {
      if (ts.isIdentifier(name)) return reference(name.text, at);
      if (ts.isQualifiedName(name)) return { kind: 'member', base: typeName(name.left), name: name.right.text };
      return null;
    }
    return { kind: 'instance', base: typeName(node.typeName) };
  }
  function expression(node, at, thisClass) {
    if (!node) return null;
    if (ts.isIdentifier(node)) return reference(node.text, at);
    if (node.kind === ts.SyntaxKind.ThisKeyword)
      return thisClass?.object ? { kind: 'object', key: thisClass.key }
        : thisClass ? { kind: thisClass.static ? 'class' : 'instance', base: { kind: 'declaration', key: thisClass.key } } : null;
    if (ts.isPropertyAccessExpression(node))
      return { kind: 'member', base: expression(node.expression, at, thisClass), name: node.name.text };
    if (ts.isElementAccessExpression(node) && ts.isStringLiteralLike(node.argumentExpression))
      return { kind: 'member', base: expression(node.expression, at, thisClass), name: node.argumentExpression.text };
    if (ts.isNewExpression(node)) return { kind: 'instance', base: expression(node.expression, at, thisClass) };
    if (ts.isParenthesizedExpression(node) || ts.isNonNullExpression(node))
      return expression(node.expression, at, thisClass);
    return null;
  }
  function bind(at, name, value) {
    if (name) data.scopes[at].bindings[name] = value;
  }
  function bindPattern(at, node, value) {
    if (ts.isIdentifier(node)) bind(at, node.text, value);
    else for (const element of node.elements) if (ts.isBindingElement(element)) bindPattern(at, element.name, null);
  }
  function addDeclaration(node, name, kind, parent) {
    const start = source.getLineAndCharacterOfPosition(node.getStart(source));
    const end = source.getLineAndCharacterOfPosition(node.getEnd());
    const key = `${kind}:${name}:${node.getStart(source)}`;
    const enclosing = parent && data.declarations.find(item => item.key === parent);
    const declaration = {
      key, name, kind, parent, qualifiedName: enclosing ? `${enclosing.qualifiedName}.${name}` : name,
      startLine: start.line + 1, endLine: end.line + 1,
      source: node.getText(source), docstring: (node.jsDoc || []).map(doc => doc.getText(source)).join('\n')
    };
    data.declarations.push(declaration);
    return key;
  }
  function exportDeclaration(node, name, value) {
    if (modifiers(node, ts.ModifierFlags.Export))
      data.exports[modifiers(node, ts.ModifierFlags.Default) ? 'default' : name] = value;
  }
  function functionNode(node, at, parent, thisClass, suggestedName, member = false) {
    const position = source.getLineAndCharacterOfPosition(node.getStart(source));
    const name = suggestedName || nameOf(node.name) || `<anonymous@${position.line + 1}:${position.character + 1}>`;
    const key = addDeclaration(node, name, member ? 'Method' : 'Function', parent);
    const value = { kind: 'declaration', key };
    if (ts.isFunctionDeclaration(node)) {
      if (node.body || !own(data.scopes[at].bindings, name)) bind(at, name, value);
      exportDeclaration(node, name, value);
    }
    const inner = scope(at, true);
    if ((ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node)) && node.name && ts.isIdentifier(node.name))
      bind(inner, node.name.text, value);
    for (const parameter of node.parameters) bindPattern(inner, parameter.name, typeExpression(parameter.type, inner));
    const context = member || ts.isArrowFunction(node) ? thisClass : null;
    for (const parameter of node.parameters) {
      if (member && thisClass && ts.isConstructorDeclaration(node) && modifiers(parameter, ts.ModifierFlags.ParameterPropertyModifier)) {
        const parameterName = nameOf(parameter.name);
        if (parameterName) data.classes[thisClass.key].instance[parameterName] = typeExpression(parameter.type, inner);
      }
      if (parameter.initializer) visit(parameter.initializer, inner, key, context);
    }
    if (node.body) visit(node.body, inner, key, context);
    return value;
  }
  function classNode(node, at, parent) {
    const name = nameOf(node.name) || 'default';
    const key = addDeclaration(node, name, 'Class', parent);
    const value = { kind: 'declaration', key };
    if (ts.isClassDeclaration(node)) {
      bind(at, name, value);
      exportDeclaration(node, name, value);
    }
    const inner = scope(at);
    bind(inner, name, value);
    const members = { instance: Object.create(null), static: Object.create(null) };
    data.classes[key] = members;
    for (const member of node.members) {
      const memberName = ts.isConstructorDeclaration(member) ? 'constructor' : nameOf(member.name);
      if (!memberName) continue;
      const isStatic = modifiers(member, ts.ModifierFlags.Static);
      const context = { key, static: isStatic };
      const table = isStatic ? members.static : members.instance;
      if (ts.isMethodDeclaration(member) || ts.isConstructorDeclaration(member)) {
        const method = functionNode(member, inner, key, context, memberName, true);
        if (member.body || !own(table, memberName)) table[memberName] = method;
      } else if (ts.isPropertyDeclaration(member)) {
        if (member.initializer && (ts.isArrowFunction(member.initializer) || ts.isFunctionExpression(member.initializer)))
          table[memberName] = functionNode(member.initializer, inner, key, context, memberName, true);
        else {
          table[memberName] = expression(member.initializer, inner, context) || typeExpression(member.type, inner);
          if (member.initializer) visit(member.initializer, inner, key, context);
        }
      }
    }
    return value;
  }
  function objectNode(node, at, parent, thisClass, name) {
    const key = `object:${node.getStart(source)}`;
    const members = Object.create(null);
    data.objects[key] = members;
    let uncertain = false;
    for (const property of node.properties) {
      const memberName = nameOf(property.name);
      if (!memberName || ts.isSpreadAssignment(property)) { uncertain = true; continue; }
      const functionValue = ts.isMethodDeclaration(property) ? property
        : ts.isPropertyAssignment(property) && (ts.isFunctionExpression(property.initializer) || ts.isArrowFunction(property.initializer))
          ? property.initializer : null;
      if (functionValue) {
        const context = ts.isArrowFunction(functionValue) ? thisClass : { key, object: true };
        const value = functionNode(functionValue, at, parent, context, memberName, true);
        const declaration = data.declarations.find(item => item.key === value.key);
        if (name) declaration.qualifiedName = `${name}.${memberName}`;
        members[memberName] = value;
      } else if (ts.isShorthandPropertyAssignment(property)) members[memberName] = reference(memberName, at);
      else if (ts.isPropertyAssignment(property)) {
        members[memberName] = expression(property.initializer, at, thisClass);
        visit(property.initializer, at, parent, thisClass);
      }
    }
    if (uncertain) for (const memberName of Object.keys(members)) members[memberName] = null;
    return { kind: 'object', key };
  }
  const writes = [];
  function recordWrite(node, at, thisClass) {
    if (ts.isArrayLiteralExpression(node))
      for (const element of node.elements) recordWrite(element, at, thisClass);
    else if (ts.isObjectLiteralExpression(node))
      for (const property of node.properties) {
        if (ts.isShorthandPropertyAssignment(property)) recordWrite(property.name, at, thisClass);
        else if (ts.isPropertyAssignment(property)) recordWrite(property.initializer, at, thisClass);
        else if (ts.isSpreadAssignment(property)) recordWrite(property.expression, at, thisClass);
      }
    else if (ts.isSpreadElement(node) || ts.isParenthesizedExpression(node)) recordWrite(node.expression, at, thisClass);
    else if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.EqualsToken) recordWrite(node.left, at, thisClass);
    else writes.push({ target: expression(node, at, thisClass), at });
  }
  function visit(node, at, parent, thisClass) {
    if (ts.isImportDeclaration(node)) {
      if (!ts.isStringLiteralLike(node.moduleSpecifier)) return;
      const module = node.moduleSpecifier.text;
      const pos = source.getLineAndCharacterOfPosition(node.getStart(source));
      data.imports.push({ module, line: pos.line + 1, column: pos.character + 1 });
      const clause = node.importClause;
      if (clause?.name) bind(at, clause.name.text, { kind: 'import', module, name: 'default' });
      if (clause?.namedBindings) {
        if (ts.isNamespaceImport(clause.namedBindings))
          bind(at, clause.namedBindings.name.text, { kind: 'namespace', module });
        else for (const item of clause.namedBindings.elements)
          bind(at, item.name.text, { kind: 'import', module, name: (item.propertyName || item.name).text });
      }
      return;
    }
    if (ts.isExportDeclaration(node)) {
      const module = node.moduleSpecifier && ts.isStringLiteralLike(node.moduleSpecifier) ? node.moduleSpecifier.text : null;
      if (module) {
        const pos = source.getLineAndCharacterOfPosition(node.getStart(source));
        data.imports.push({ module, line: pos.line + 1, column: pos.character + 1 });
      }
      if (node.exportClause && ts.isNamedExports(node.exportClause))
        for (const item of node.exportClause.elements)
          data.exports[item.name.text] = module
            ? { kind: 'import', module, name: (item.propertyName || item.name).text }
            : reference((item.propertyName || item.name).text, at);
      return;
    }
    if (ts.isExportAssignment(node)) {
      let value;
      if (ts.isArrowFunction(node.expression) || ts.isFunctionExpression(node.expression))
        value = functionNode(node.expression, at, parent, thisClass, 'default');
      else if (ts.isClassExpression(node.expression))
        value = classNode(node.expression, at, parent);
      else if (ts.isObjectLiteralExpression(node.expression))
        value = objectNode(node.expression, at, parent, thisClass, 'default');
      else {
        value = expression(node.expression, at, thisClass);
        visit(node.expression, at, parent, thisClass);
      }
      if (!node.isExportEquals) data.exports.default = value;
      return;
    }
    if (ts.isClassDeclaration(node) || ts.isClassExpression(node)) { classNode(node, at, parent); return; }
    if (ts.isFunctionDeclaration(node) || ts.isFunctionExpression(node) || ts.isArrowFunction(node)) {
      functionNode(node, at, parent, thisClass); return;
    }
    if (ts.isVariableDeclaration(node)) {
      let bindingScope = at;
      if (ts.isVariableDeclarationList(node.parent) && !(node.parent.flags & ts.NodeFlags.BlockScoped))
        while (!data.scopes[bindingScope].functionScope) bindingScope = data.scopes[bindingScope].parent;
      const name = nameOf(node.name);
      let value;
      if (node.initializer && (ts.isArrowFunction(node.initializer) || ts.isFunctionExpression(node.initializer)))
        value = functionNode(node.initializer, at, parent, thisClass, name);
      else if (node.initializer && ts.isClassExpression(node.initializer))
        value = classNode(node.initializer, at, parent);
      else if (node.initializer && ts.isObjectLiteralExpression(node.initializer))
        value = objectNode(node.initializer, at, parent, thisClass, name);
      else {
        value = expression(node.initializer, at, thisClass) || typeExpression(node.type, at);
        if (node.initializer) visit(node.initializer, at, parent, thisClass);
      }
      bindPattern(bindingScope, node.name, value);
      if (name && node.parent.parent && ts.isVariableStatement(node.parent.parent))
        exportDeclaration(node.parent.parent, name, reference(name, bindingScope));
      return;
    }
    if (ts.isBlock(node) || ts.isCatchClause(node) || ts.isForStatement(node) || ts.isForOfStatement(node) || ts.isForInStatement(node)) {
      const inner = scope(at);
      if ((ts.isForOfStatement(node) || ts.isForInStatement(node)) && !ts.isVariableDeclarationList(node.initializer))
        recordWrite(node.initializer, inner, thisClass);
      ts.forEachChild(node, child => visit(child, inner, parent, thisClass));
      return;
    }
    if (ts.isCallExpression(node)) {
      const pos = source.getLineAndCharacterOfPosition(node.expression.getStart(source));
      data.calls.push({ caller: parent, target: expression(node.expression, at, thisClass), line: pos.line + 1, column: pos.character + 1 });
    }
    if (ts.isBinaryExpression(node) && node.operatorToken.kind >= ts.SyntaxKind.FirstAssignment && node.operatorToken.kind <= ts.SyntaxKind.LastAssignment)
      recordWrite(node.left, at, thisClass);
    if ((ts.isPrefixUnaryExpression(node) || ts.isPostfixUnaryExpression(node)) &&
        (node.operator === ts.SyntaxKind.PlusPlusToken || node.operator === ts.SyntaxKind.MinusMinusToken))
      recordWrite(node.operand, at, thisClass);
    if (ts.isDeleteExpression(node)) recordWrite(node.expression, at, thisClass);
    ts.forEachChild(node, child => visit(child, at, parent, thisClass));
  }
  visit(source, top, null, null);
  // A mutable binding is not enough evidence for a call target, even if its initial value is known.
  for (const { target, at } of writes) {
    if (target?.kind === 'name') {
      for (let current = at; current !== null; current = data.scopes[current].parent)
        if (Object.hasOwn(data.scopes[current].bindings, target.name)) {
          data.scopes[current].bindings[target.name] = null;
          break;
        }
    } else if (target?.kind === 'member') {
      data.invalidMembers.push(target.name);
      // Conservatively invalidate writes by property name, including aliases of an instance.
      for (const members of Object.values(data.classes))
        for (const table of [members.instance, members.static])
          if (Object.hasOwn(table, target.name)) table[target.name] = null;
    }
  }
  for (const diagnostic of source.parseDiagnostics) {
    const pos = source.getLineAndCharacterOfPosition(diagnostic.start || 0);
    data.warnings.push(`${relative}:${pos.line + 1}: ${ts.flattenDiagnosticMessageText(diagnostic.messageText, ' ')}`);
  }
  return data;
}

function link(files) {
  const symbols = [], edges = [], warnings = [];
  const invalidMembers = new Set(Object.values(files).flatMap(entry => entry.data.invalidMembers));
  const resolveModule = (from, specifier) => {
    if (!specifier.startsWith('./') && !specifier.startsWith('../')) return null;
    const base = path.posix.normalize(path.posix.join(path.posix.dirname(from), specifier));
    if (base === '..' || base.startsWith('../') || path.posix.isAbsolute(base)) return null;
    const extension = path.posix.extname(base).toLowerCase();
    const candidates = extension === '.js' || extension === '.jsx'
      ? [base.slice(0, -extension.length) + '.ts', base.slice(0, -extension.length) + '.tsx', base]
      : extension ? [base] : [base + '.ts', base + '.tsx', base + '.js', base + '.jsx',
        base + '/index.ts', base + '/index.tsx', base + '/index.js', base + '/index.jsx'];
    return candidates.find(candidate => Object.hasOwn(files, candidate)) || null;
  };
  function resolve(file, value, seen = new Set()) {
    if (!value || seen.size > 100) return null;
    const token = file + '\0' + JSON.stringify(value);
    if (seen.has(token)) return null;
    seen = new Set(seen).add(token);
    const data = files[file].data;
    if (value.kind === 'object') return { kind: 'object', file, key: value.key };
    if (value.kind === 'declaration') {
      const declaration = data.declarations.find(item => item.key === value.key);
      return declaration ? { kind: declaration.kind === 'Class' ? 'class' : 'callable', file, key: value.key } : null;
    }
    if (value.kind === 'name') {
      for (let at = value.scope; at !== null; at = data.scopes[at].parent)
        if (Object.hasOwn(data.scopes[at].bindings, value.name))
          return resolve(file, data.scopes[at].bindings[value.name], seen);
      return null;
    }
    if (value.kind === 'import' || value.kind === 'namespace') {
      const target = resolveModule(file, value.module);
      if (!target) return null;
      return value.kind === 'namespace' ? { kind: 'namespace', file: target }
        : resolve(target, own(files[target].data.exports, value.name), seen);
    }
    if (value.kind === 'instance' || value.kind === 'class') {
      const target = resolve(file, value.base, seen);
      return target?.kind === 'class' ? { ...target, kind: value.kind } : null;
    }
    if (value.kind === 'member') {
      const target = resolve(file, value.base, seen);
      if (target?.kind === 'namespace')
        return resolve(target.file, own(files[target.file].data.exports, value.name), seen);
      if (target?.kind === 'class' || target?.kind === 'instance') {
        if (invalidMembers.has(value.name)) return null;
        const members = own(files[target.file].data.classes, target.key);
        return members ? resolve(target.file, own(members[target.kind === 'class' ? 'static' : 'instance'], value.name), seen) : null;
      }
      if (target?.kind === 'object' && !invalidMembers.has(value.name)) {
        const members = own(files[target.file].data.objects, target.key);
        return members ? resolve(target.file, own(members, value.name), seen) : null;
      }
    }
    return null;
  }
  for (const [relative, entry] of Object.entries(files)) {
    const data = entry.data;
    warnings.push(...data.warnings);
    for (const declaration of data.declarations) {
      const { key, parent, ...metadata } = declaration;
      const id = declarationId(relative, key);
      const parentId = parent ? declarationId(relative, parent) : fileId(relative);
      symbols.push({ ...metadata, id, filePath: relative, parentId });
      edges.push({ fromId: parentId, toId: id, kind: 'DEFINES', siteLine: null, siteColumn: null });
    }
    for (const imported of data.imports) {
      const target = resolveModule(relative, imported.module);
      if (target) edges.push({ fromId: fileId(relative), toId: fileId(target), kind: 'IMPORTS', siteLine: imported.line, siteColumn: imported.column });
    }
    for (const call of data.calls) {
      const target = resolve(relative, call.target);
      if (target?.kind === 'callable')
        edges.push({
          fromId: call.caller ? declarationId(relative, call.caller) : fileId(relative),
          toId: declarationId(target.file, target.key), kind: 'CALLS', siteLine: call.line, siteColumn: call.column
        });
    }
  }
  return { symbols, edges, warnings };
}

function main() {
  const request = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
  const root = path.resolve(request.root);
  checkLinks(root);
  const cachePath = outputPath(request.cachePath, root);
  const resultPath = outputPath(request.outputPath, root);
  const previousPath = request.previousCachePath && outputPath(request.previousCachePath, root);
  let previous = null;
  const warnings = [];
  if (previousPath && fs.existsSync(previousPath)) {
    try {
      const candidate = JSON.parse(fs.readFileSync(previousPath, 'utf8'));
      if (candidate.schema === schema && candidate.compiler === ts.version) previous = candidate;
      else warnings.push('TypeScript cache version changed; reparsing whitelisted files.');
    } catch { warnings.push('TypeScript cache is unreadable; reparsing whitelisted files.'); }
  }
  const files = Object.create(null);
  let parsedFiles = 0, reusedFiles = 0;
  for (const relative of [...new Set(request.paths)].sort()) {
    if (typeof relative !== 'string' || relative.includes('\\') || relative.includes(':') ||
        relative.split('/').some(part => !part || part === '.' || part === '..') ||
        !['.ts', '.tsx', '.js', '.jsx'].includes(path.posix.extname(relative).toLowerCase()))
      throw new Error(`Unsafe source path: ${relative}`);
    const full = path.resolve(root, ...relative.split('/'));
    if (!within(full, root)) throw new Error(`Source path escapes root: ${relative}`);
    checkLinks(full);
    if (!fs.statSync(full).isFile()) throw new Error(`Source is not a regular file: ${relative}`);
    const bytes = fs.readFileSync(full);
    const contentHash = hash(bytes);
    const cached = previous?.files && own(previous.files, relative);
    if (cached && cached.hash === contentHash && cached.data) {
      files[relative] = cached;
      reusedFiles++;
    } else {
      files[relative] = { hash: contentHash, data: parse(relative, bytes.toString('utf8')) };
      parsedFiles++;
    }
  }
  const result = link(files);
  result.warnings.push(...warnings);
  result.parsedFiles = parsedFiles;
  result.reusedFiles = reusedFiles;
  fs.writeFileSync(resultPath, JSON.stringify(result), { flag: 'wx' });
  const staging = cachePath + '.' + crypto.randomUUID() + '.staging';
  try {
    fs.writeFileSync(staging, JSON.stringify({ schema, compiler: ts.version, files }), { flag: 'wx' });
    fs.renameSync(staging, cachePath);
  } finally {
    if (fs.existsSync(staging)) fs.unlinkSync(staging);
  }
}

try { main(); }
catch (error) {
  process.stderr.write(`TypeScript indexing failed: ${error.message}\n`);
  process.exitCode = 1;
}
