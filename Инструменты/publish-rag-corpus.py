"""Publish a reviewed standalone dataset release, independently of application updates.

Preview by default. --publish requires prior user authorization. Large archives
remain release assets, never Git objects. Existing public releases are immutable.
Authentication is delegated to gh; no credential is read or printed here.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


def gh(*args):
    result = subprocess.run(['gh', *args], check=True, capture_output=True, text=True, encoding='utf-8')
    return result.stdout


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--repo', default='PiTrolKun/LOPATA')
    parser.add_argument('--publish', action='store_true')
    args = parser.parse_args()
    delivery = json.loads((args.catalog/'delivery.json').read_text(encoding='utf-8-sig'))
    tag = delivery['tag']
    files = [args.directory/name for name in [delivery['filename'], 'corpus.json', 'README.md',
                                             'SHA256SUMS.txt', 'verification.json', 'delivery.json']]
    expected = {p.name: dict(size=p.stat().st_size, digest='sha256:'+digest(p)) for p in files}
    if digest(files[0]) != delivery['sha256'] or files[0].stat().st_size != delivery['bytes']:
        raise ValueError('Archive does not match reviewed delivery')
    if digest(args.directory/'corpus.json') != delivery['corpus_sha256']:
        raise ValueError('Corpus manifest mismatch')
    if any(v['size'] >= 2*1024**3 for v in expected.values()):
        raise ValueError('GitHub asset size limit')
    print(json.dumps(dict(tag=tag, commit=args.commit, files=expected, publish=args.publish)), flush=True)
    if not args.publish:
        return
    remote = json.loads(gh('api', f'repos/{args.repo}/commits/{args.commit}'))
    if remote['sha'] != args.commit:
        raise ValueError('Source commit is not present remotely')
    probe = subprocess.run(['gh','api',f'repos/{args.repo}/releases/tags/{tag}'], capture_output=True)
    if probe.returncode:
        # A network/auth failure must never be mistaken for a missing release.
        releases = json.loads(gh('api', f'repos/{args.repo}/releases?per_page=100'))
        if any(r['tag_name'] == tag for r in releases):
            raise ValueError('Release lookup failed for existing tag')
        gh('release','create',tag,'--repo',args.repo,'--target',args.commit,'--draft','--prerelease',
           '--latest=false','--title','LOPATA Rhyme RU/EN 2026.10.15 — RAG dataset',
           '--notes-file',str(args.directory/'release-notes.md'))
    release = json.loads(gh('api', f'repos/{args.repo}/releases/tags/{tag}'))
    if not release['draft']:
        raise ValueError('Public releases cannot be replaced')
    ref = json.loads(gh('api', f'repos/{args.repo}/git/ref/tags/{tag}'))
    if ref['object']['type'] != 'commit' or ref['object']['sha'] != args.commit:
        raise ValueError('Tag/commit mismatch')
    actual = {a['name']:a for a in release['assets']}
    if set(actual)-set(expected):
        raise ValueError('Unexpected assets in draft')
    for path in files:
        if path.name in actual:
            if any(actual[path.name].get(k) != v for k,v in expected[path.name].items()):
                raise ValueError('Existing draft asset mismatch: '+path.name)
        else:
            print('Uploading '+path.name, flush=True)
            gh('release','upload',tag,str(path),'--repo',args.repo)
    def verify_assets():
        result=json.loads(gh('api', f'repos/{args.repo}/releases/tags/{tag}'))
        if len(result['assets']) != len(expected):
            raise ValueError('Asset count mismatch')
        for item in result['assets']:
            if item['name'] not in expected or any(item.get(k)!=v for k,v in expected[item['name']].items()):
                raise ValueError('Server size/SHA256 mismatch')
        return result
    verify_assets()
    gh('release','edit',tag,'--repo',args.repo,'--draft=false','--prerelease','--latest=false')
    result=verify_assets()
    if result['draft'] or not result['prerelease']:
        raise ValueError('Release not published as requested')
    (args.directory/'release-verified.json').write_text(json.dumps(
        dict(url=result['html_url'],id=result['id'],commit=args.commit,tag=tag,
             draft=result['draft'],prerelease=result['prerelease'],assets=expected),
        ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('Published and verified: '+result['html_url'],flush=True)


if __name__ == '__main__':
    main()
