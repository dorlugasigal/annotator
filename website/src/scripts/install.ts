import {motionAllowed, token} from "./motion-tokens";

// Homebrew, and winget when the site has a Windows listing, each get a Copy button.
for (const root of document.querySelectorAll<HTMLElement>("[data-command-copy]")) {
  const button = root.querySelector<HTMLButtonElement>("[data-copy-button]");
  const command = root.querySelector<HTMLElement>("[data-copy-command]");
  const status = root.querySelector<HTMLElement>("[data-copy-status]");
  const copied = root.dataset.copiedMessage;
  if (!button || !command?.textContent?.trim() || !status || !copied) throw new Error("An install command block is incomplete.");
  const value = command.textContent.trim();
  const swipe = root.querySelector<HTMLElement>("[data-copy-swipe]");
  let settle: number | undefined;
  button.hidden = false;
  button.addEventListener("click", async () => {
    button.disabled = true;
    status.hidden = false;
    try {
      await navigator.clipboard.writeText(value);
      status.textContent = copied;
      root.dataset.copied = "true";
      window.clearTimeout(settle);
      settle = window.setTimeout(() => { delete root.dataset.copied; }, 2400);
      if (swipe && motionAllowed()) {
        swipe.animate([
          {clipPath: "inset(0 100% 0 0)", opacity: 1},
          {clipPath: "inset(0 0 0 0)", opacity: 1, offset: .55},
          {clipPath: "inset(0 0 0 0)", opacity: 0},
        ], {duration: token("--beat-double"), easing: token("--ease-ink", "string")});
      }
    } catch (error) {
      status.textContent = "Copy didn’t work. Select the command and copy it.";
      console.info("Clipboard access is unavailable.", error);
    } finally {
      button.disabled = false;
    }
  });
}
export {};
