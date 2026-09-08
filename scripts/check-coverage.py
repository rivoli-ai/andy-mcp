#!/usr/bin/env python3
"""Fail closed on missing or regressed critical Cobertura coverage. No dependencies."""
import argparse
import glob
import re
import sys
import xml.etree.ElementTree as ET

GATES = {
    'protocol': ('Andy.MCP/Protocol/', 95, 80),
    'transport': ('Andy.MCP/Transport/', 85, 78),
    'auth': ('Andy.MCP/Auth/', 85, 73),
    'http-server': ('Andy.MCP.AspNetCore/', 90, 78),
    'client': ('Andy.MCP/Client/', 80, 70),
    'server': ('Andy.MCP/Server/', 85, 75),
}

def evaluate(root):
    failures = []
    for name, (prefix, min_line, min_branch) in GATES.items():
        lines, branches = {}, {}
        for cls in root.findall('.//class'):
            filename = cls.get('filename', '').replace('\\', '/')
            if prefix not in filename:
                continue
            for line in cls.findall('./lines/line'):
                key = (filename, int(line.attrib['number']))
                lines[key] = max(lines.get(key, 0), int(line.attrib['hits']))
                if line.get('branch', '').lower() == 'true':
                    match = re.fullmatch(r'.*\((\d+)/(\d+)\)', line.attrib['condition-coverage'])
                    if not match: raise ValueError('Invalid branch coverage: ' + str(line.attrib))
                    hit, count = map(int, match.groups())
                    if count <= 0 or hit > count: raise ValueError('Invalid branch counts')
                    prior = branches.get(key, (0, count))
                    if prior[1] != count: raise ValueError('Inconsistent duplicated branch counts')
                    branches[key] = (max(prior[0], hit), count)
        if not lines or not branches:
            failures.append(name + ': missing line or branch coverage')
            continue
        line_rate = 100 * sum(v > 0 for v in lines.values()) / len(lines)
        branch_rate = 100 * sum(v[0] for v in branches.values()) / sum(v[1] for v in branches.values())
        print(f'{name}: lines {line_rate:.2f}% >= {min_line}%; branches {branch_rate:.2f}% >= {min_branch}%')
        if line_rate < min_line or branch_rate < min_branch:
            failures.append(name + ': coverage threshold failed')
    return failures

def self_test():
    root = ET.Element('coverage')
    assert len(evaluate(root)) == len(GATES)
    for prefix, _, _ in GATES.values():
        cls = ET.SubElement(root, 'class', filename=prefix.replace('/', '\\') + 'Fixture.cs')
        lines = ET.SubElement(cls, 'lines')
        ET.SubElement(lines, 'line', number='1', hits='1', branch='True', **{'condition-coverage': '100% (2/2)'})
    assert not evaluate(root)
    for line in root.findall('.//line'):
        line.set('hits', '0'); line.set('condition-coverage', '0% (0/2)')
    assert len(evaluate(root)) == len(GATES)
    print('Coverage gate self-test passed: missing/zero coverage fails; valid Windows paths pass.')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', nargs='?', help='Exact report or glob matching exactly one fresh report')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        self_test(); sys.exit(0)
    paths = glob.glob(args.report or '', recursive=True)
    if len(paths) != 1:
        parser.error(f'Expected exactly one fresh Cobertura report; found {len(paths)}. Use a clean results directory.')
    failures = evaluate(ET.parse(paths[0]).getroot())
    if failures:
        print('\n'.join(failures), file=sys.stderr)
        sys.exit(1)
