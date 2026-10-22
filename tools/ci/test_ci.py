from pathlib import Path
import re
import unittest

try:
    import yaml
except ImportError:
    yaml = None


ROOT = Path(__file__).resolve().parents[2]
VALIDATE = (ROOT / ".github/workflows/validate.yml").read_text()
RELEASE = (ROOT / ".github/workflows/release.yml").read_text()


def job(source, name):
    return re.search(rf"^  {name}:\n(.*?)(?=^  [\w-]+:\n|\Z)",
                     source, re.M | re.S).group(1)


def condition(block, values):
    expression = re.search(r"if: \$\{\{ (.+) \}\}", block).group(1)
    # Evaluate the deliberately small routing grammar, not arbitrary code.
    def term(text):
        if text == "always()":
            return True
        if " != " in text:
            key, value = text.split(" != ")
            return values[key] != value.strip("'")
        if " == " in text:
            key, value = text.split(" == ")
            return values[key] == value.strip("'")
        return values[text]
    return any(all(term(part) for part in text.split(" && ")) for text in expression.split(" || "))


class ValidationWorkflowTests(unittest.TestCase):
    def test_routing_truth_table(self):
        # Validation always runs, and a release starts only from Run workflow: a push to main never releases.
        self.assertNotRegex(VALIDATE.split("jobs:\n")[1].split("steps:")[0], r"\n    if:")
        triggers = RELEASE.split("\non:\n")[1].split("\npermissions:")[0]
        self.assertIn("workflow_dispatch:", triggers)
        for event in ("push:", "pull_request", "schedule:", "workflow_run:"):
            self.assertNotIn(event, triggers)
        self.assertNotIn("'push'", RELEASE)
        self.assertNotIn("== push", RELEASE)
        self.assertNotIn("if:", job(RELEASE, "validate"))
        self.assertNotRegex(job(RELEASE, "preflight").split("steps:")[0], r"\n    if:")
        for flag, name in (("true", True), ("false", False), ("", False)):
            for requested in (True, False):
                values = {"inputs.notarize": requested, "vars.RELEASE_AUTOMATION_ENABLED": flag}
                self.assertEqual(condition(job(RELEASE, "sign"), values), requested and name)
                for windows in ("success", "skipped"):
                    values = {"inputs.publish": requested, "vars.PUBLICATION_ENABLED": flag,
                              "needs.sign.result": "success", "needs.windows-package.result": windows}
                    self.assertEqual(condition(job(RELEASE, "publish"), values), requested and name)
                for blocked in ({"needs.sign.result": "failure", "needs.windows-package.result": "success"},
                                {"needs.sign.result": "success", "needs.windows-package.result": "failure"},
                                {"needs.sign.result": "success", "needs.windows-package.result": "cancelled"}):
                    values = {"inputs.publish": True, "vars.PUBLICATION_ENABLED": "true", **blocked}
                    self.assertFalse(condition(job(RELEASE, "publish"), values))

    def test_hosted_validation_scope(self):
        # Guards always run; the Mac and Windows test jobs run when their paths change, and on every release validation.
        self.assertEqual(re.findall(r"^  ([\w-]+):$", VALIDATE.split("jobs:\n")[1], re.M), ["guards", "macos", "windows"])
        self.assertNotIn("full:", VALIDATE)
        self.assertNotRegex(job(VALIDATE, "guards").split("steps:")[0], r"\n    if:")
        for name, os_runner in (("macos", "macos-26"), ("windows", "windows-2025-vs2026")):
            block = job(VALIDATE, name)
            self.assertIn("needs: guards", block)
            self.assertIn(f"if: ${{{{ needs.guards.outputs.{name} == 'true' && !github.event.repository.private }}}}", block)
            self.assertIn(f"runs-on: {os_runner}", block)
            self.assertIn("persist-credentials: false", block)
            self.assertNotIn("secrets.", block)
        self.assertIn("make test", job(VALIDATE, "macos"))
        self.assertIn("make conformance-check-data", job(VALIDATE, "macos"))
        self.assertIn("validate.ps1", job(VALIDATE, "windows"))
        for workflow in (ROOT / ".github/workflows").glob("*.yml"):
            source = workflow.read_text()
            # Browser matrices, benchmarks, WACK and on-screen native suites stay on contributors' machines.
            for command in ("playwright install", "test:e2e", "test:pages", "test:distribution",
                            "benchmark.sh", "appcert", "native-test", "live-check.ps1"):
                self.assertNotIn(command, source, str(workflow))
        for command in ("swift test", "make test", "validate.ps1", "dotnet test"):
            self.assertNotIn(command, RELEASE)
        self.assertIn("runs-on: macos-26", job(RELEASE, "sign"))

    def test_source_filters_cannot_skip_release_guards(self):
        guards = job(VALIDATE, "guards")
        self.assertIn("RELEASE_VALIDATION: ${{ inputs.release-validation }}", guards)
        guard_step = guards.split("- name: Release, packaging and CI guards")[1].split("- uses:")[0]
        self.assertNotIn("if:", guard_step)
        for script in ("test_package_app.py", "test_release.py", "test_distribution.py", "test_ci_scope.py"):
            self.assertIn(script, guard_step)
        self.assertIn("if: steps.scope.outputs.website == 'true'", guards)
        self.assertIn("if: steps.scope.outputs.metrics == 'true'", guards)
        self.assertIn("working-directory: services/metrics", guards)

    def test_release_waits_for_exact_run_even_if_preflight_fails(self):
        validate = job(RELEASE, "validate")
        self.assertNotIn("needs:", validate)
        self.assertIn("uses: ./.github/workflows/validate.yml", validate)
        self.assertIn("release-validation: true", validate)
        sign = job(RELEASE, "sign")
        self.assertIn("needs: [preflight, validate]", sign)
        # Without a status override GitHub applies success() to this job:
        # failure, cancellation and skipped dependencies cannot authorize signing.
        expression = re.search(r"if: (.+)", sign).group(1)
        for override in ("always(", "cancelled(", "failure(", "!success("):
            self.assertNotIn(override, expression)
        self.assertIn('CANDIDATE_SHA: ${{ inputs.candidate_sha }}', job(RELEASE, "preflight"))
        self.assertNotIn("ref:", VALIDATE)

    def test_local_browser_projects_remain(self):
        config = (ROOT / "website/playwright.config.mjs").read_text()
        for project in ("chromium", "webkit", "mobile"):
            self.assertIn(f'name: "{project}"', config)

    def test_cache_boundary_and_action_pins(self):
        self.assertNotIn("actions/cache", RELEASE)
        self.assertNotIn("cache: true", RELEASE)
        self.assertNotIn("restore-keys:", VALIDATE)
        self.assertNotIn("ms-playwright", VALIDATE)
        self.assertNotIn("node_modules", VALIDATE)
        for action in re.findall(r"uses: ([^\s]+)", VALIDATE + RELEASE):
            if not action.startswith("./"):
                self.assertRegex(action, r"@[a-f0-9]{40}$")

    def test_release_and_publishing_workflows_share_one_pin_per_action(self):
        pins = {}
        for name in ("release.yml", "website.yml", "distribution-sync.yml"):
            for action, sha in re.findall(r"uses: ([\w./-]+)@([^\s]+)",
                                          (ROOT / ".github/workflows" / name).read_text()):
                self.assertRegex(sha, r"^[a-f0-9]{40}$", f"{name}: {action}")
                if action != "actions/setup-node":
                    pins.setdefault(action, set()).add(sha)
        for action, shas in pins.items():
            self.assertEqual(len(shas), 1, action)

    def test_secrets_reach_steps_only_through_env(self):
        for workflow in (ROOT / ".github/workflows").glob("*.yml"):
            for line in workflow.read_text().splitlines():
                if "secrets." in line:
                    self.assertRegex(line, r"^\s+[A-Z][A-Z0-9_]*: \$\{\{ secrets\.[A-Z][A-Z0-9_]* \}\}$", str(workflow))
            self.assertNotRegex(workflow.read_text(), r"(echo|Write-Host|print).*\$(env:)?\w*(SECRET|PASSWORD|TOKEN|PRIVATE_KEY)")


class WindowsReleaseTests(unittest.TestCase):
    def test_windows_costs_nothing_until_a_release_or_the_owner_asks(self):
        build = job(RELEASE, "windows-package")
        self.assertIn("needs: [preflight, validate]", build)
        self.assertIn("if: ${{ needs.preflight.outputs.windows == 'true' }}", build)
        self.assertIn("runs-on: windows-2025-vs2026", build)
        self.assertIn("timeout-minutes:", build)
        self.assertNotIn("environment:", build)
        self.assertNotIn("secrets.", build)
        plan = job(RELEASE, "preflight").split("- name: Decide the Windows part of this run")[1]
        for value in ("RELEASE_PROCEEDS: ${{ inputs.notarize }}",
                      "STORE_TARGET: ${{ inputs.windows_store }}", "BUILD_WINDOWS: ${{ inputs.windows_packages }}",
                      "python3 tools/windows/hosted_release.py plan"):
            self.assertIn(value, plan)

    def test_the_windows_job_builds_with_the_existing_scripts_and_only_cheap_guards(self):
        build = job(RELEASE, "windows-package")
        for step in ("hosted_release.py pins", "actions/setup-dotnet@", "dotnet-version: ${{ steps.pins.outputs.dotnet }}",
                     "hosted_release.py toolchain", "test_publish_windows.py", "test_hosted_release.py",
                     "hosted_release.py guards", "tools/windows/package.ps1", "'-RequireArm64'",
                     "hosted_release.py receipt", "name: windows-bundle", "if-no-files-found: error",
                     "'-Portable'", "test_winget.py", "winget.py describe", "winget.py verify", "name: windows-portable"):
            self.assertIn(step, build)
        for expensive in ("validate.ps1", "dotnet test", "make ", "appcert", "benchmarks", "a11y"):
            self.assertNotIn(expensive, build)
        guards = build.split("- name: Windows release guards")[1].split("- name:")[0]
        self.assertEqual(guards.count("if ($LASTEXITCODE) { exit $LASTEXITCODE }"), 3)

    def test_the_store_needs_the_owners_manual_run_and_the_protected_environment(self):
        store = job(RELEASE, "windows-store")
        self.assertIn("needs: [preflight, windows-package]", store)
        self.assertIn("environment: windows-store", store)
        expression = re.search(r"if: \$\{\{ (.+) \}\}", store).group(1)
        for clause in ("needs.preflight.outputs.windows-store != 'none'", "github.event_name == 'workflow_dispatch'",
                       "github.triggering_actor == github.repository_owner", "vars.STORE_SUBMISSION_ENABLED == 'true'"):
            self.assertIn(clause, expression)
        self.assertNotIn("||", expression)
        for override in ("always(", "cancelled(", "failure(", "!success("):
            self.assertNotIn(override, expression)
        self.assertIn("hosted_release.py verify", store)
        self.assertIn("hosted_release.py cli", store)
        for name in ("PARTNER_CENTER_TENANT_ID", "PARTNER_CENTER_SELLER_ID", "PARTNER_CENTER_CLIENT_ID",
                     "PARTNER_CENTER_CLIENT_SECRET"):
            self.assertEqual(RELEASE.count(f"secrets.{name}"), 1)
            self.assertIn(f"{name}: ${{{{ secrets.{name} }}}}", store)
        dispatch = RELEASE.split("windows_store:")[1].split("permissions:")[0]
        self.assertIn("default: none", dispatch)
        self.assertIn("options: [none, testers, public]", dispatch)

    def test_signing_keeps_the_archive_sent_to_apple_on_failure(self):
        sign = job(RELEASE, "sign")
        self.assertIn("--recovery build/distribution/notarization-recovery", sign)
        recovery = sign.split("- name: Keep the archive sent to Apple when signing stops")[1]
        self.assertIn("if: ${{ failure() }}", recovery)
        self.assertIn("path: build/distribution/notarization-recovery/", recovery)
        self.assertIn("METRICS_URL: ${{ vars.METRICS_URL }}", sign)

    def test_every_release_job_has_a_time_limit(self):
        for name in ("preflight", "sign", "publish", "windows-package", "windows-store"):
            self.assertRegex(job(RELEASE, name), r"timeout-minutes: \d+", name)


@unittest.skipUnless(yaml, "PyYAML is not installed")
class WorkflowStructureTests(unittest.TestCase):
    def workflows(self):
        for path in sorted((ROOT / ".github/workflows").glob("*.yml")):
            data = yaml.safe_load(path.read_text())
            data["on"] = data.pop(True, data.get("on"))
            yield path.name, data

    def test_jobs_reference_only_declared_jobs_outputs_steps_and_inputs(self):
        for name, data in self.workflows():
            jobs = data["jobs"]
            trigger = data["on"] if isinstance(data["on"], dict) else {}
            inputs = set()
            for event in ("workflow_dispatch", "workflow_call"):
                inputs |= set(((trigger.get(event) or {}).get("inputs") or {}))
            for job_name, body in jobs.items():
                needs = body.get("needs", [])
                needs = [needs] if isinstance(needs, str) else needs
                for need in needs:
                    self.assertIn(need, jobs, f"{name}: {job_name} needs {need}")
                text = yaml.safe_dump(body, width=10**6)
                for need, output in re.findall(r"needs\.([\w-]+)\.outputs\.([\w-]+)", text):
                    self.assertIn(need, needs, f"{name}: {job_name} reads {need} without needing it")
                    self.assertIn(output, jobs[need].get("outputs", {}), f"{name}: {need} has no output {output}")
                ids = {step.get("id") for step in body.get("steps", [])}
                for step, _ in re.findall(r"steps\.([\w-]+)\.outputs\.([\w-]+)", text):
                    self.assertIn(step, ids, f"{name}: {job_name} has no step {step}")
                for used in re.findall(r"inputs\.([\w-]+)", text):
                    self.assertIn(used, inputs, f"{name}: {job_name} reads an unknown input {used}")

    def test_permissions_stay_read_only_except_explicit_dispatch_rights(self):
        for name, data in self.workflows():
            self.assertEqual(data["permissions"]["contents"], "read", name)
            extra = {key: value for key, value in data["permissions"].items() if key != "contents"}
            self.assertEqual(extra, {"actions": "write"} if name == "distribution-sync.yml" else {}, name)
            for job_name, body in data["jobs"].items():
                if (name, job_name) in (("pages.yml", "deploy"), ("website.yml", "bridge")):
                    # The Pages feed-bridge deploy is the only job that writes, and only to GitHub Pages.
                    self.assertEqual(body["permissions"], {"contents": "read", "pages": "write", "id-token": "write"})
                    self.assertEqual(body["environment"]["name"], "github-pages")
                    continue
                if (name, job_name) == ("release.yml", "publish"):
                    # Publishing creates the release in this repository with the workflow token,
                    # behind the release-publishing environment.
                    self.assertEqual(body["permissions"], {"contents": "write"})
                    self.assertEqual(body["environment"], "release-publishing")
                    continue
                self.assertNotIn("permissions", body, f"{name}: {job_name}")

    def test_website_deploys_only_sealed_main_builds(self):
        data = dict(self.workflows())["website.yml"]
        self.assertNotIn("pull_request", data[True] if True in data else data["on"])
        build, deploy, bridge = (data["jobs"][name] for name in ("build", "deploy", "bridge"))
        self.assertIn("vars.WEBSITE_DEPLOY_ENABLED == 'true'", build["if"])
        self.assertNotIn("secrets.", yaml.safe_dump(build))
        self.assertEqual(deploy["environment"], "website-publishing")
        self.assertIn("vars.NETLIFY_SITE_ID != ''", deploy["if"])
        self.assertIn("github.ref == 'refs/heads/main'", deploy["if"])
        self.assertIn("github.ref == 'refs/heads/main'", bridge["if"])
        self.assertNotIn("secrets.", yaml.safe_dump(bridge))
        # The site is never committed to the repository; Pages gets an uploaded artifact.
        self.assertNotIn("pages.py sync", yaml.safe_dump(data))

    def test_release_runs_one_at_a_time_without_cancelling(self):
        data = dict(self.workflows())["release.yml"]
        self.assertEqual(data["concurrency"], {"group": "annotator-release", "cancel-in-progress": False})
        self.assertEqual(data["on"]["workflow_dispatch"]["inputs"]["windows_store"]["options"], ["none", "testers", "public"])


if __name__ == "__main__":
    unittest.main()
