"""Hosted website deploy: the Netlify site and the old GitHub Pages feed bridge, from GitHub Actions.

build   After `distribution.py website`, `npm ci` in website/ and services/metrics, and
        `npm --prefix website run build:netlify`, seal the Netlify artifact and the bridge.
deploy  Deploy the sealed Netlify artifact with NETLIFY_AUTH_TOKEN, verify the live service, then wait
        until the live homepage, downloads page, dashboard and update feeds match the sealed files.
verify  Wait until the live feed bridge matches its sealed files (after actions/deploy-pages).

The bridge is uploaded with actions/upload-pages-artifact and deployed with actions/deploy-pages,
so nothing is committed to the repository.
"""
import argparse
import os
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import distribution as d  # noqa: E402
import local_release as local  # noqa: E402
import netlify_release as netlify  # noqa: E402
import publish_release as publisher  # noqa: E402

INTERVAL, TIMEOUT = 15, 600


class Log:
    @staticmethod
    def log(message):
        print(message, flush=True)


def _source(value):
    if not re.fullmatch(r"[0-9a-f]{40}", value or ""):
        raise d.DistributionError("The source must be a full commit SHA.")
    return value


def build(folder, site, legacy, source):
    folder = Path(folder)
    if folder.exists():
        raise d.DistributionError("Use a fresh website folder.")
    artifact = netlify.prepare(local.ROOT, folder / "netlify", site, _source(source))
    netlify.prepare_bridge(artifact, folder / "bridge", legacy, site, source)
    return folder


def deploy(folder, site, source, ident, environ=os.environ):
    if environ.get("WEBSITE_DEPLOY_ENABLED") != "true" or environ.get("GITHUB_REF") != "refs/heads/main":
        raise d.DistributionError("Website deploys need WEBSITE_DEPLOY_ENABLED and the main branch.")
    if not environ.get("NETLIFY_AUTH_TOKEN"):
        raise d.DistributionError("Set the NETLIFY_AUTH_TOKEN secret in the website-publishing environment.")
    cli = local.ROOT / "services/metrics/node_modules/.bin/netlify"
    deployment = netlify.deploy(Path(folder) / "netlify", site, _source(source), ident, cli)
    publisher.verify_site(Path(folder) / "netlify", netlify.site_url(site), source, Log, INTERVAL, TIMEOUT, netlify_host=True)
    return deployment


def verify_bridge(folder, legacy, source):
    publisher.verify_site(Path(folder) / "bridge", d.website_url(legacy), _source(source), Log, INTERVAL, TIMEOUT)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("build", "deploy", "verify"):
        p = sub.add_parser(name)
        p.add_argument("--folder", type=Path, required=True)
        p.add_argument("--site", required=name != "verify")
        p.add_argument("--source", required=True)
        if name != "deploy":
            p.add_argument("--legacy", default=netlify.LEGACY_SITE)
        if name == "deploy":
            p.add_argument("--site-id", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "build":
            build(args.folder, args.site, args.legacy, args.source)
            print(f"Sealed the website and the update-feed bridge in {args.folder}.")
        elif args.command == "deploy":
            deployment = deploy(args.folder, args.site, args.source, args.site_id)
            print(f"Netlify deployment {deployment} is live and verified.")
        else:
            verify_bridge(args.folder, args.legacy, args.source)
    except (d.DistributionError, OSError, ValueError, KeyError) as error:
        print(f"Website stopped: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
