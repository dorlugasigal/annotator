#!/usr/bin/env python3
"""Publish only the Netlify website and metrics functions. Plan by default."""
import argparse
import os
from pathlib import Path
import re
import sys

import publish_release as publisher

d = publisher.d
local = publisher.local
netlify = publisher.netlify
STAGES = ("Preflight", "Build website locally", "Sync website", "Verify live website")


def config(path):
    raw = local.read_json(local.safe_path(path))
    required = {"repository", "homepage", "github_user", "netlify_site_id"}
    if required - raw.keys() or raw.keys() - publisher.REQUIRED - publisher.OPTIONAL:
        raise d.DistributionError("Use the documented website config or existing local publisher config.")
    keys = required | {"metrics_url"}
    settings = {key: value for key, value in raw.items() if key in keys}
    if any(not isinstance(value, str) or not value.strip() for value in settings.values()):
        raise d.DistributionError("Website config values must be nonempty strings.")
    settings["repository"] = d.repository(settings["repository"])
    if settings["repository"] == "dorlugasigal/annotator-code":
        raise d.DistributionError("Use the public repository, not the archived private dorlugasigal/annotator-code.")
    settings["homepage"] = netlify.site_url(settings["homepage"])
    settings["netlify_site_id"] = netlify.site_id(settings["netlify_site_id"])
    if not re.fullmatch(r"[A-Za-z0-9-]+", settings["github_user"]):
        raise d.DistributionError("Supply the GitHub account that can read published releases.")
    if settings.get("metrics_url"):
        settings["metrics_url"] = netlify.site_url(settings["metrics_url"])
        if settings["metrics_url"] != settings["homepage"]:
            raise d.DistributionError("Website metrics must use the website's origin.")
    return settings


def execute(args, settings, progress):
    if os.environ.get("GITHUB_ACTIONS") == "true":
        raise d.DistributionError("Website publishing must run locally.")
    source = d.command("git", "-C", local.ROOT, "rev-parse", "HEAD").strip()
    if d.command("git", "-C", local.ROOT, "status", "--porcelain").strip():
        raise d.DistributionError("Commit the website changes before publishing.")
    binding = {"source": source, **settings}
    binding_path = progress.directory / "website.json"
    if binding_path.exists():
        if local.read_json(binding_path) != binding:
            raise d.DistributionError("Saved website source or destination differs. Use a fresh folder.")
    else:
        local.write_once(binding_path, binding)
    cli = local.ROOT / "services/metrics/node_modules/.bin/netlify"
    with progress.stage("Preflight"):
        netlify.check_remote(cli, settings["netlify_site_id"], settings["homepage"])
        token = d.command("gh", "auth", "token", "--user", settings["github_user"]).strip()
        if not token:
            raise d.DistributionError("GitHub CLI returned no credential for the selected account.")
        progress.hidden.append(token)
    with publisher.environment({
        "GH_TOKEN": token, "DISTRIBUTION_REPOSITORY": settings["repository"],
        "WEBSITE_URL": settings["homepage"], "SITE_URL": settings["homepage"], "CONTEXT": "production",
    }):
        if d.gh_json("api", f"repos/{settings['repository']}").get("private") is not False:
            raise d.DistributionError("Website downloads must come from a public release repository.")
        with progress.stage("Build website locally"):
            artifact = progress.directory / "package"
            if artifact.exists():
                netlify.verify(artifact, settings["homepage"], source)
                progress.log("Using the saved, verified website artifact.")
            else:
                artifact = publisher.build_website(progress.directory, source, settings["homepage"], settings)
        deployed_path = progress.directory / "deployment.json"
        with progress.stage("Sync website"):
            if deployed_path.exists():
                saved = local.read_json(deployed_path)
                if saved.get("source") != source or not isinstance(saved.get("deploy_id"), str):
                    raise d.DistributionError("Saved website deployment does not match the source.")
                netlify.verify_service(cli, settings["netlify_site_id"], saved["deploy_id"], settings["homepage"])
                progress.log(f"Verifying saved Netlify deployment: {saved['deploy_id']}")
            else:
                deployment = netlify.deploy(artifact, settings["homepage"], source, settings["netlify_site_id"], cli)
                local.write_once(deployed_path, {"source": source, "deploy_id": deployment})
                progress.log(f"Netlify deployment: {deployment}")
        with progress.stage("Verify live website"):
            publisher.verify_site(artifact, settings["homepage"], source, progress,
                                  args.interval, args.site_timeout, netlify_host=True)
    progress.log(f"Website: {settings['homepage']}")
    progress.log("Website and metrics functions published. App releases, Homebrew and the legacy Pages site were not changed.")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=Path("build/local-publish-config.json"))
    parser.add_argument("--folder", type=Path, help="Saved artifact/log directory; defaults to build/website-publish-SOURCE.")
    parser.add_argument("--execute", action="store_true", help="Deploy the website/functions; never sign or publish an app.")
    parser.add_argument("--interval", type=int, default=15)
    parser.add_argument("--site-timeout", type=int, default=900)
    args = parser.parse_args(argv)
    progress = None
    try:
        settings = config(args.config)
        print(f"Website: {settings['homepage']}")
        if args.interval < 10 or args.site_timeout < 1:
            raise d.DistributionError("Use interval >= 10 and a positive site timeout.")
        if not args.execute:
            print("PLAN ONLY. Builds from committed source and current published app releases.")
            print("Add --execute to publish the Netlify website and metrics functions only.")
            print("No app version bump, signing keys, Apple submission, release upload or Homebrew update.")
            return 0
        source = d.command("git", "-C", local.ROOT, "rev-parse", "HEAD").strip()
        folder = local.safe_path(args.folder or local.ROOT / "build" / f"website-publish-{source[:12]}")
        folder.mkdir(parents=True, mode=0o700, exist_ok=True)
        progress = publisher.Progress(folder, stages=STAGES)
        progress.hidden.extend(value for name, value in os.environ.items()
                               if name.endswith(("_TOKEN", "_PASSWORD", "_SECRET", "_PRIVATE_KEY")))
        with local.locked(folder):
            log = folder / "pipeline.log"
            if log.exists():
                local.ordinary(log)
            fd = os.open(log, os.O_CREAT | os.O_APPEND | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "a") as stream, publisher.command_logs(progress):
                progress.output = stream
                progress.log(f"Log: {log}")
                execute(args, settings, progress)
        return 0
    except KeyboardInterrupt:
        print("Stopped. Saved website artifacts were kept; rerun the same command to resume.", file=sys.stderr)
        return 130
    except d.DistributionError as error:
        message = progress.clean(str(error)) if progress else str(error)
        print(f"Website publication stopped: {message}", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError):
        print("Website publication stopped: invalid local files or tool response. Inspect pipeline.log.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
