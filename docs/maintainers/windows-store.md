# Microsoft Store submission kit: Annotator for Windows

For the maintainer who owns the Partner Center account. It covers
WIN-DST-01 to WIN-DST-08 and decision D-7 in [the parity contract](../parity.md).
The first submission is done; later builds go through the Release workflow
([6](#6-send-builds-from-github-actions)).

Only the account owner enrolls, reserves the name, uploads packages and
submits. The packages are unsigned, and the Store signs what it publishes.

For the next submission, the description below ends with the source link, and
**Additional license terms** stays empty: the app's code licence (Apache-2.0)
is on GitHub, and the Store's standard terms cover the Store download.

## Annotator's Store values

On 2026-10-20 you enrolled and reserved **Annotator – Screen Annotation** as an
MSIX or PWA app (the name Annotator was not used). Product identity, as the
coordinator read it at 21:00:

| Value | Annotator's value |
| --- | --- |
| `Package/Identity/Name` | `DorLugasi-Gal.AnnotatorScreenAnnotation` |
| `Package/Identity/Publisher` | `CN=9BC8A074-AAD1-4E16-8AC2-B68940F537CD` |
| `Package/Properties/PublisherDisplayName` | `Dor Lugasi-Gal` |
| Store ID | `9NMZ345ZJ6JK` |
| Package Family Name | `DorLugasi-Gal.AnnotatorScreenAnnotation_es65rrkpa6e10` |

They are in `Package.appxmanifest` from the 0.1.0-beta.8 candidate on, and
`python tools/windows/publish_windows.py --check-store --store-id 9NMZ345ZJ6JK`
says `READY`. The package's display name and its Start menu name
(`uap:VisualElements` `DisplayName`) are the reserved name, so Start and
Settings > Apps show **Annotator – Screen Annotation**. Annotator's own
windows, About, tray icon, Start tile label and startup task still say
Annotator ([recorded decisions](../parity.md#recorded-owner-decisions)).

The repository variable `WINDOWS_STORE_ID` is `9NMZ345ZJ6JK` (set on
2026-10-21). The first submission passed certification and the listing has been
public since 2026-10-21, so the website's `storeId` is set too
([5.4](#54-the-launch-decision)). People install it with:

```powershell
winget install --id 9NMZ345ZJ6JK --source msstore
```

Partner Center changes its pages from time to time. The labels below come from
Microsoft Learn on 2026-10-20 ([sources](#sources)). If a label looks
different, look for the same words nearby.

## Your part, with times

| Step | What you do | Time |
| --- | --- | --- |
| 1 | Enroll as an individual developer, if Apps and games is missing ([1.2](#12-enroll-as-an-individual-developer)) | 10 to 15 minutes, then Microsoft's identity check |
| 2 | Reserve the name and copy the product identity ([1.3](#13-create-the-product-and-reserve-the-name), [1.4](#14-copy-the-product-identity)) | 5 minutes |
| 3 | Paste the values, run the check and commit ([2](#2-where-each-value-goes)) | 5 minutes |
| 4 | Add Visual Studio's ARM64 build tools, once ([5.2](#52-the-first-submission-step-by-step)) | 10 minutes, mostly downloading |
| 5 | Approve the candidate version, then run the publisher ([5.2](#52-the-first-submission-step-by-step)) | 5 minutes to start; it runs alone, and WACK needs an administrator terminal |
| 6 | Create the tester group ([5.2](#52-the-first-submission-step-by-step)) | 5 minutes |
| 7 | Fill in the submission with the text on this page, then submit ([3](#3-submission-fields-ready-to-paste)) | 20 to 30 minutes; certification can take up to three business days |
| 8 | After the private release: add a package flight for each new tester build ([5.3](#53-tester-builds-after-the-first-release)) | 5 minutes each |
| 9 | Later, when you decide to launch: make the listing public and set the website's Store ID ([5.4](#54-the-launch-decision)) | 15 minutes |
| 10 | Optional, after the first release: let GitHub Actions send tester builds ([6](#6-send-builds-from-github-actions)) | 20 minutes once, then one manual run per build |

## 1. In Partner Center

You are in Partner Center's account settings. You don't need the Certificates
page, because the Store signs Annotator's packages.

### 1.1 Check for Apps and games

Open the Partner Center home page (<https://partner.microsoft.com/dashboard/home>).
If you see **Apps and games**, your account is already in the Microsoft Store
developer program. Go to [1.3](#13-create-the-product-and-reserve-the-name).
If you don't see it, enroll first.

### 1.2 Enroll as an individual developer

Individual accounts are free. Microsoft checks your identity with a photo of a
government ID and a selfie.

Decide your publisher display name before you start (D-7). The Store shows it
as the publisher under the app's name. For example, use your name as it
appears in Annotator's copyright line, Dor Lugasi-Gal. If you change it after
your first upload, Product identity changes too, and every later package must
use the new value. To check or change it later, select the gear icon >
**Account settings** > **Organization profile** > **Legal profile** >
**Developer** > **Publisher display name**.

1. Open <https://storedeveloper.microsoft.com> in the same browser. Microsoft
   says this page is the only way into the current sign-up.
2. Select **Get started for free**, then **Individual developer**.
3. Sign in with the personal Microsoft account that will own Annotator's
   listing.
4. Select **Begin identity verification**. Follow the steps on your phone: a
   photo of your ID, then a selfie.
5. Complete your profile details.
6. Select **Go to Partner Center dashboard**. You arrive on the
   **Apps & Games overview** page. If Partner Center doesn't show Apps and
   games yet, wait about five minutes and refresh the page.

### 1.3 Create the product and reserve the name

1. Open **Apps and games** (or <https://aka.ms/submitwindowsapp>).
2. Select **New product**, then **MSIX or PWA app**.
3. Type `Annotator` and select **Check availability**. (Done: you reserved
   **Annotator – Screen Annotation**.)
4. If you see a green check, select **Reserve product name**.

The reservation ends after three months unless you submit the app before
then.

If someone else reserved Annotator, choose another name yourself (D-7). It
can't be a trademark you don't own, and it can't hold a version number, a date
or an emoji. For example, check `Annotator - Draw on Screen`. Then change the
package to match in [2](#2-where-each-value-goes). Annotator's own windows
still say Annotator. If you hold a trademark on the name, Microsoft asks you to
[contact them](https://www.microsoft.com/info/cpyrtInfrg.html).

### 1.4 Copy the product identity

In the product, expand **Product management** and select **Product identity**.
Copy these four values. Partner Center compares them exactly, including
capital letters, spaces and punctuation.

| Value on Product identity | What it looks like |
| --- | --- |
| `Package/Identity/Name` | Letters, digits and periods |
| `Package/Identity/Publisher` | Starts with `CN=` |
| `Package/Properties/PublisherDisplayName` | Your publisher display name |
| Store ID | 12 capital letters and digits, such as `9NBLGGH4R315` |

The same page shows the private link for testers: **URL if your app is only
visible to certain people (requires authentication)**. You need it in
[5.2](#52-the-first-submission-step-by-step).

## 2. Where each value goes

| Value | Where it goes | When |
| --- | --- | --- |
| `Package/Identity/Name` | `Name` of `<Identity>` in `apps/windows/src/Annotator.App/Package.appxmanifest`, in place of `PLACEHOLDER.Annotator` | Now |
| `Package/Identity/Publisher` | `Publisher` of `<Identity>`, in place of `CN=PLACEHOLDER-PARTNER-CENTER-PUBLISHER-ID` | Now |
| `Package/Properties/PublisherDisplayName` | `<PublisherDisplayName>`, in place of `PLACEHOLDER Partner Center publisher name` | Now |
| The reserved name, only if it isn't Annotator | `<DisplayName>` under `<Properties>` and `DisplayName` of `<uap:VisualElements>` (now `Annotator – Screen Annotation`) | Now |
| Store ID | `--store-id` when you run `tools/windows/publish_windows.py` | Each publisher run |
| Store ID | `storeId` in `website/src/data/windows-listing.json` | Only when the listing is public |

Keep `Version="0.0.0.0"` in the manifest, because the build sets the real
version. Annotator's values went in on 2026-10-20 (see
[Annotator's Store values](#annotators-store-values)).

Then check every value with one command. It changes nothing:

```powershell
python tools/windows/publish_windows.py --check-store --store-id <Store ID>
```

It prints each value where the checkout keeps it and ends with `READY` or
`NOT READY`. It finds PLACEHOLDER or empty values, spaces around a pasted
value, a package name with characters a package can't have, a publisher
without `CN=`, a Start menu name that isn't the package's display name, a
malformed Store ID, and a website Store ID that isn't the one you give. It
can't see typing mistakes inside a value; Partner Center finds those when you
upload. Commit the values with the candidate, because the publisher starts
only from a clean checkout.

The website and its help get everything from `storeId`. While it is `null`,
the site names no Windows download (WIN-DST-08). Set it only when anyone can
open the listing ([5.4](#54-the-launch-decision)). Then the home page shows a
"Get it for Windows" link to `https://apps.microsoft.com/detail/<Store ID>`
with the requirements, and Downloads and Help show the winget command
(WIN-DST-02):

```powershell
winget install --id <Store ID> --source msstore
```

The same setting adds Windows key names where pages list shortcuts, Snipping
Tool on the AI page and the "Microsoft Store and Windows" section on Privacy.
Nothing else needs the Store ID. The app finds its updates through its package
identity, and its own help shows no install command.

## 3. Submission fields, ready to paste

On Annotator's overview page in Partner Center, start the submission. The
sections below follow its pages. Use the
[first-submission order](#52-the-first-submission-step-by-step): the audience
must be private the first time.

### Pricing and availability

| Field | Choose |
| --- | --- |
| Markets | Keep the default, all possible markets, unless you decide otherwise |
| Audience | **Private audience** for the first submission, with the tester group from [5.2](#52-the-first-submission-step-by-step). At launch: **Public audience** |
| Discoverability (Public audience only) | **Make this product available and discoverable in the Store** |
| Schedule | Keep the default: as soon as possible |
| Pricing | **Free**: Currency **USD - United States**, Retail price **0** (the list has no word Free) |
| Free trial | **No free trial** |
| Sale pricing, organizational licensing | Keep the defaults |

Once a submission goes out with Public audience, later submissions can't go
back to Private audience. Private audience members need personal Microsoft
accounts; work and school accounts don't work.

On 2026-10-21 you chose **Public audience** for the first submission
instead, with publishing as soon as it passes certification, so no tester
group was needed. Choosing Private audience asks to clear the Schedule dates.

### Properties

| Field | Value |
| --- | --- |
| Category | Productivity |
| Subcategory | None |
| Secondary category | Optional: Multimedia design |
| Privacy policy URL | `https://getannotator.app/privacy/` |
| Website | `https://getannotator.app/` |
| Support contact info | `https://getannotator.app/support/` |
| System requirements | Leave the hardware items empty. Every Windows 11 PC has the graphics and memory that Annotator needs |

The package itself limits installs to Windows 11 version 22H2 (build 22621) or
later, and the bundle holds the x64 and ARM64 packages.

If Partner Center asks whether the product accesses, collects or transmits
personal information, answer **Yes**. Annotator reads screen pixels on the PC
for zoom, the pixel effects and region sharing, and Microsoft wants a privacy
policy for every Win32 app (Store policy 10.5.1).

Product declarations:

- Keep these two on, as Partner Center sets them: **Customers can install this
  product to alternate drives or removable storage** and **Windows can include
  this product's data in automatic backups to OneDrive**.
- Leave **This product allows users to make purchases, but does not use the
  Microsoft Store commerce system** off. Annotator sells nothing. The Buy me a
  coffee link opens a website, and a contribution unlocks nothing.
- Leave **This product has been tested to meet accessibility guidelines** off
  until the Narrator, keyboard-only, contrast theme and 225% text checks pass
  on Windows (acceptance record).
- Leave **This product supports pen and ink input** off until a pen test on
  Windows passes.
- Leave **This product incorporates generative AI features** off. Annotator
  has none; its AI skill only helps an assistant that the person runs.

### Age ratings

The questionnaire comes from IARC. Microsoft doesn't publish its exact
questions, and they change with your answers. Use these answers by topic:

1. For the first question, choose the category for utility or productivity
   apps, not Game or social networking.
2. Answer **No** to every question about violence, blood, fear, sexual
   content, nudity, bad language, crude humor, drugs, alcohol, tobacco,
   gambling and hate symbols. Annotator has no content of its own; it shows
   what the person draws.
3. Users interact or exchange content with other users: **No**. Annotator has
   no chat and no sharing service. Region sharing shows a window on the
   person's own PC, and their meeting app sends it.
4. Shares the person's location: **No**.
5. Digital purchases: **No**.
6. Unrestricted internet access, such as a web browser or search engine:
   **No**. Links open the person's own browser.
7. Shares personal information with third parties: **No**.
8. Select **Save and generate**.

Expect the lowest rating in each region, such as 3+, but IARC decides. If the
result is higher, check the answers before you appeal.

### Packages

1. Drag the bundle from the publisher's release folder onto the Packages page:
   `<release folder>\packages\<MSIX version>\Annotator_<MSIX version>.msixbundle`.
   Upload only the bundle, not the single `.msix` files next to it.
2. Under device family availability, select only **Windows 10/11 Desktop**.
   Clear Xbox, Holographic and Team if Partner Center selected them. Leave the
   box that lets Microsoft offer the app to future device families cleared.
3. Every upload needs a higher version than the one before. The publisher
   takes it from the shared version and build: `(Major + 1).Minor.Build.0`,
   for example `1.1.26.0` for build 26 (WIN-DST-04).

### Store listings

Choose **English (United States)**, the package's language. Under
**Product name**, choose **Annotator – Screen Annotation**, the name you
reserved.

**Description** (up to 10,000 characters):

```text
Annotator lets you draw on your screen while you explain something. Add arrows, shapes, highlights and text over any app, then move, restyle or delete each mark later.

Use it for demos, code reviews, lessons and calls. Press Ctrl+2 to show the toolbar, then pick a tool and draw. Select Use apps to work in your app again, and your drawings stay on the screen. Annotator runs from the system tray, near the clock.

WHAT YOU CAN DO
- Draw with the pen, highlighter, line, arrow, rectangle, ellipse and text tools.
- Sketch a rough box or arrow, and Smart Draw makes it neat.
- Click to place arrow points, then drag a point to bend the arrow.
- Use sketchy or dashed lines, fills and gradients, or draw on a whiteboard or blackboard.
- Pick from more than 40 tools: shapes such as a diamond, star or person, diagram icons such as a server, database or laptop, a pixel ruler, numbered counters and images.
- Keep your own shapes in My shapes.
- Show where you point with Spotlight, a cursor halo and click effects.
- Zoom into the screen around your pointer with Ctrl+1, or magnify a small area with the pointer lens.
- Blur, pixelate or distort part of the live screen, or hide it under a solid cover. Blur isn't secure redaction.
- Share part of a display, such as its left half, in its own window. Choose that window in your meeting app, so text stays easy to read.
- Save your drawings as a board and open it again later. Boards use the same file format as Annotator for Mac.
- Export a drawing as a PNG image.
- Choose the toolbar buttons, keyboard shortcuts, pointer style and show and hide animation in Settings.

FREE AND OPEN SOURCE
All tools are free. There is no account, sign-in or advertising. Annotator is open source under the Apache 2.0 license: https://github.com/dorlugasigal/annotator. From time to time, Annotator asks if you'd like to buy the developer a coffee. That's optional and unlocks nothing.

YOUR PRIVACY
Your drawings, screen pixels and typing stay on your computer. Screen zoom, the pointer lens, the pixel effects and region sharing read the screen only while they run. Annotator doesn't record or save video.

BEFORE YOU SHARE YOUR SCREEN
If you share one app or window in a meeting, others may not see Annotator's drawings and covers. Ask someone on the call what they see. Hiding or quitting Annotator removes covers, so remove private details from your material first.

Requires Windows 11, version 22H2 or later.
```

**What's new in this version** (up to 1,500 characters; first private
submission):

```text
The first test version of Annotator for Windows.
```

**Product features** (up to 20, each up to 200 characters):

```text
Draw arrows, shapes, highlights and text over any app.
Move, restyle or delete every mark after you draw it.
Smart Draw turns a rough box or arrow into a neat shape.
More than 40 tools, including diagram icons, a pixel ruler and numbered counters.
Spotlight, a cursor halo and click effects show where you point.
Screen zoom and a pointer lens that follow your pointer.
Blur, pixelate or cover part of the live screen. Blur isn't secure redaction.
Share part of a display in its own window with region sharing.
Save boards and export PNG images.
Choose your own toolbar, shortcuts and pointer style.
Free, with no account and no ads.
Your drawings, screen pixels and typing stay on your computer.
```

**Screenshots**: see [4](#4-screenshots).

**Store logos**: optional. The Store uses the package's logo when you upload
none. If you want the 1:1 app tile icon (300 x 300), upload
`apps/windows/src/Annotator.App/Assets/Package/Square150x150Logo.scale-200.png`.

**Short description** (up to 1,000 characters; the Store shows the first 270):

```text
Draw on your screen while you explain. Add arrows, shapes, highlights and text over any app, and keep every mark editable. For demos, reviews, lessons and calls. All tools are free, with no account.
```

**Search terms** (up to 7):

```text
annotate
draw on screen
marker
presentation
highlighter
whiteboard
screen zoom
```

Partner Center refuses a third keyword with a word that two keywords already use, without a message: "screen marker" did not stick next to "draw on screen" and "screen zoom", so the first submission uses "marker". Type each keyword and press Enter; Backspace in the empty box removes the last keyword.

| Field | Value |
| --- | --- |
| Copyright and trademark info | `© 2026 Dor Lugasi-Gal. Open source under the Apache License 2.0.` (the line in Annotator's About) |
| Additional license terms | Leave empty. The Store's standard application license terms apply |
| Developed by | Leave empty. The Store shows your publisher display name |
| Additional system requirements, minimum hardware | Not on the listing page in October 2026; the package sets Windows 11 22H2, x64 and ARM64 |
| Short title (up to 50 characters) | `Annotator` |
| Sort title, voice title, trailers | Leave empty |

### Submission options

**Publishing hold options**: for the private first submission, keep
**Publish this submission as soon as it passes certification**. At launch, you
can choose **Don't publish this submission until I select Publish now** to
pick the moment yourself.

**Notes for certification**: in October 2026 they are on the product's
**Additional Testing Information** page (Supplemental info), not in
Submission options. Paste:

```text
Annotator is a desktop app for drawing on the live screen during demos and calls. It needs no account or sign-in and has no paid features.

How to test:
1. Start Annotator from Start. The toolbar and a short welcome tour appear. Annotator's icon is in the system tray, near the clock; Windows may put it under the up arrow.
2. Select a tool, such as Pen or Arrow, and drag anywhere on the screen to draw. Press Escape or select Use apps to work in other apps again; the drawings stay visible. Ctrl+2 shows or hides the toolbar and drawings.
3. Screen zoom (Ctrl+1), the pointer lens, Blur, Pixelate, Distort and region sharing read the screen while they run. The first time, Annotator asks Windows for permission to capture the screen; please allow it.
4. To quit, right-click the tray icon and select Quit.

Launch at sign-in stays off until the person turns it on in Settings. Help, Report a problem and the optional Buy me a coffee link open the browser; a contribution unlocks nothing. Apart from Microsoft Store update checks through Windows and links the person opens, Annotator makes no network requests.
```

**Restricted capabilities**: Partner Center asks about `runFullTrust`, the
package's only restricted capability. The box takes at most 500 characters. Paste:

```text
Annotator, a desktop app packaged with MSIX, draws on the live desktop in transparent windows above other apps and registers global shortcuts. A low-level mouse hook runs while its toolbar or zoom shows (Alt+scroll zoom); a keyboard hook runs only during zoom (Escape). Zoom, the lens, Blur, Pixelate, Distort and region sharing read the screen with the Magnification API and Windows.Graphics.Capture, only while they run and after the person allows it. Optional start at sign-in. Data stays local.
```

The two capture capabilities, `graphicsCaptureProgrammatic` and
`graphicsCaptureWithoutBorder`, are general-use capabilities, so Partner Center
asks no reason for them. If it asks anyway, paste the same text.

## 4. Screenshots

The Store needs at least one desktop screenshot and takes up to ten: PNG
files from 1366 x 768 to 3840 x 2160 pixels, up to 50 MB each, with captions
of up to 200 characters. Microsoft suggests four or more.

Repository rules for marketing pictures: capture the real app's desktop
overlay over sample windows that the capture owns, never test fixtures or
image-backed canvases. Use only made-up content, and never capture your own
windows.

Capture plan, on the development PC (5120 x 1440 pixels at 125%):

1. Build the development app: `pwsh -NoProfile -File tools/windows/validate.ps1 app`.
2. Pick an area that becomes the screenshot: `0,0,2400,1350` pixels, which is
   1920 x 1080 at 125% and stays above the taskbar.
3. Open a sample window that covers that area exactly and shows made-up
   content: a slide with a request-flow diagram, and a contact card with a
   fictional name and a 555 phone number for the blur scene.
4. Start the real toolbar and canvas over the same area, beside any running
   Annotator, with no tray icon, global shortcuts or saved settings:
   `apps\windows\out\app\Annotator.exe --live-check --area 0,0,2400,1350 --out <evidence folder> --timeout 120`.
5. Draw with real mouse input into Annotator's own windows only, after
   `WindowFromPoint` confirms each target, as `tools/windows/live-check.ps1`
   does.
6. Save each scene with Windows.Graphics.Capture, which writes only that
   area: `Annotator.RenderHarness.exe shot --area 0,0,2400,1350 --evidence <scene>.png`.
7. Check every picture. It must show only the sample window, the toolbar and
   the drawings, with no other window, notification or pointer. Keep the
   pictures outside the repository.

Scenes and captions:

| Scene | On screen | Caption |
| --- | --- | --- |
| 1 | Arrows, an ellipse, a highlight and a text label on the slide; toolbar visible | Draw over any app. Every mark stays editable. |
| 2 | The Tools window open over the slide | More than 40 tools, from shapes to diagram icons. |
| 3 | Blur over the made-up contact card | Blur part of the live screen. Blur isn't secure redaction. |
| 4 | A diagram drawn with server and database icons and Smart Draw arrows | Smart Draw turns rough sketches into neat shapes. |
| 5 | Spotlight with the cursor halo over the slide | Show where you point with Spotlight and the cursor halo. |

Status on 2026-10-20: candidates for scenes 1 to 3 exist, 2400 x 1350 pixels
each, made with the integrated development build (commit `0ffd511`). They are
kept outside the repository, in the session folder
`files/evidence/store-shots/` (`store-1-draw.png`, `store-2-tools.png`,
`store-3-blur.png`). Check them before you upload them, or capture them again
from the approved candidate if the toolbar has changed. Scenes 4 and 5 are
optional and still to do.

On 2026-10-21 scenes 1 to 3 were captured again from the beta.8 source
(`4b228b4`) in the build VM on the Mac (1600 x 900 pixels, the same
`--live-check` overlay and RenderHarness `shot`, a made-up sample window,
notifications off) and uploaded with these captions. They are in
`~/Releases/annotator-windows-1.1.26.0/store-shots/`; the scripts are in
`~/VMs/annotator-win/shots/`. Partner Center processes each picture for
about ten seconds; add a caption only after the count next to Desktop goes up.

## 5. Testers, flights and the first submission

### 5.1 Why the first submission is private

Two Partner Center rules set the order:

- Package flights appear only after the app has a published submission.
- A submission that goes out with Public audience can't go back to Private
  audience.

So the first submission is the private test itself, with Private audience and
your tester group. After it is published, package flights send newer builds to
testers (WIN-DST-03). The listing stays private until your launch decision.

### 5.2 The first submission, step by step

1. Approve a new candidate version. Version 0.1.0-beta.7 (25) is already a
   published Mac release, and a label is never reused. The read-only plan
   (`python tools/release/release.py plan --change feature`) suggests 0.1.0-beta.8
   (26), which makes the MSIX version 1.1.26.0. The version is your decision.
   Once you approve it, an agent can prepare it with the annotator-release
   skill.
2. Paste the Partner Center values, run `--check-store` and commit them with
   the candidate ([2](#2-where-each-value-goes)).
3. Once: in the Visual Studio Installer, add **MSVC ARM64/ARM64EC build tools**
   (`Microsoft.VisualStudio.Component.VC.Tools.ARM64`). The Store bundle needs
   x64 and ARM64 packages.
4. Run the publisher. Keep the release folder outside the checkout:

   ```powershell
   python tools/windows/publish_windows.py --folder C:\Releases\annotator-windows-1.1.26.0
   python tools/windows/publish_windows.py --folder C:\Releases\annotator-windows-1.1.26.0 --execute --store-id 9NMZ345ZJ6JK
   ```

   The first command only plans. The second freezes the source, runs
   `validate.ps1 all`, builds the x64 and ARM64 packages and the bundle, and
   runs the Windows App Certification Kit (WACK). WACK needs an administrator
   terminal, so the publisher pauses and prints two `appcert.exe` commands.
   Run them as administrator, then run the second command again. It ends with
   the bundle's path and submits nothing.
5. Create the tester group in Partner Center: **Engage** >
   **Customer groups** > **Create new group**, choose **Known user group**,
   name it, for example "Annotator Windows testers", and add the testers'
   personal Microsoft account emails (up to 10,000, or import a .csv file).
6. Fill in the submission with [3](#3-submission-fields-ready-to-paste). Under
   Pricing and availability, choose **Private audience** and the group.
   Upload the bundle on the Packages page. Then submit. Microsoft's check can
   take up to three business days.
7. When it is published, send the testers the private link from Product
   identity ([1.4](#14-copy-the-product-identity)). Each tester signs in to the
   Microsoft Store with the invited account and installs Annotator. They need
   Windows 11 version 22H2 or later. The Store updates them automatically.

### 5.3 Tester builds after the first release

1. Approve the next candidate (a higher build) and run the publisher in a new
   release folder.
2. On Annotator's overview page, find **Package flights** and select
   **New package flight**. Name it, for example "Windows beta", and choose the
   tester group, or make a new one there with **Create a flight group**. You
   create the flight once and reuse it.
3. Upload the new bundle to the flight's submission and submit it. Testers in
   the group get the highest-ranked flight that includes them. The non-flight
   submission always ranks lowest.

You can also upload from the command line, but only after the first
submission is published. Install the Microsoft Store Developer CLI
(`winget install "Microsoft Store Developer CLI"`) and run `msstore reconfigure`
once. It needs a Microsoft Entra application from **Account settings** >
**User management** > **Microsoft Entra applications**.
`msstore flights list <Store ID>` shows each flight's ID. Then use the command
that the publisher prints:

```powershell
python tools/windows/publish_windows.py --folder <release folder> --execute --store-id <Store ID> --flight-id <flight ID>
msstore publish "<bundle>" -id <Store ID> --noCommit -f <flight ID>
```

`--noCommit` uploads the bundle and leaves a draft; it is not a dry run.
`msstore publish` replaces a draft that is waiting, so run it before you edit
the draft in Partner Center. Check the draft there, then submit.

GitHub Actions can do the upload for you instead, from the same frozen
commit: see [6](#6-send-builds-from-github-actions).

### 5.4 The launch decision

When you decide to launch:

1. Make a new submission with **Public audience** and **Make this product
   available and discoverable in the Store**. Flights keep working for testers.
2. When the listing opens signed out, set `storeId` in
   `website/src/data/windows-listing.json` to `9NMZ345ZJ6JK`, run
   `python tools/windows/publish_windows.py --check-store --store-id 9NMZ345ZJ6JK`
   and the website checks, including `npm run test:windows`, then publish the
   website with the usual owner-run publisher. The website's acceptance entry
   lists this step.

### 5.5 How the publisher fits

| Stage | `tools/windows/publish_windows.py` | You |
| --- | --- | --- |
| Before | `--check-store` checks the Partner Center values | Paste and commit them |
| 1 Freeze source | Clean checkout, version and build, release notes check, Store identity | Approve the candidate |
| 2 Validate locally | `validate.ps1 all` | Wait |
| 3 Build packages | x64 and ARM64 packages and the bundle (`package.ps1 -RequireArm64`) | Install the ARM64 build tools once |
| 4 Certify with WACK | Reads the WACK report; pauses if it needs an administrator | Run the two printed commands as administrator |
| 5 Hand off to the Store | Checks the bundle didn't change; prints the upload steps | Upload and submit in Partner Center |

`--rehearsal` runs every stage with the placeholder identity and an x64-only
bundle, and hands off nothing. Run `python tools/windows/publish_windows.py --help`
for every option.

## 6. Send builds from GitHub Actions

After your first submission is published, the Release workflow can build the
bundle on GitHub and send it to the Store for you. It never makes the first
submission, and it runs only when you start it. The
[release workflow](release-workflow.md#the-store-gate) explains the
whole gate. This section is the one-time setup.

### 6.1 A Microsoft Entra app for Partner Center

The CLI signs in to Partner Center as a Microsoft Entra app, with the app's own
secret.

1. Partner Center needs a Microsoft Entra tenant. If **Account settings** >
   **User management** > **Microsoft Entra applications** asks for one,
   associate or create it there first.
2. On that **Microsoft Entra applications** page, add a new app, name it, for
   example "Annotator GitHub", and give it the **Manager** role.
3. In the Microsoft Entra admin center (<https://entra.microsoft.com>), open
   **Identity** > **Applications** > **App registrations** > the app >
   **Certificates & secrets**. Select **New client secret** and copy its
   **Value** at once; Entra shows it only once. Note when it expires and set a
   reminder to replace it.
4. Copy these four values:

| GitHub secret | Where you find it |
| --- | --- |
| `PARTNER_CENTER_TENANT_ID` | Entra admin center > **Identity** > **Overview** > **Tenant ID** |
| `PARTNER_CENTER_CLIENT_ID` | The app registration's **Application (client) ID** |
| `PARTNER_CENTER_CLIENT_SECRET` | The client secret's **Value** from step 3 |
| `PARTNER_CENTER_SELLER_ID` | Partner Center > **Account settings**: your **Seller ID** |

### 6.2 The windows-store environment in GitHub

In the source repository (`dorlugasigal/annotator`), open
**Settings** > **Environments** > **New environment** and name it
`windows-store`.

1. **Deployment branches and tags**: if GitHub offers it, allow only `main`.
2. **Required reviewers**: if GitHub offers it, add yourself. GitHub then waits
   for your approval before the Store job starts. On a free plan, GitHub
   offers reviewers for public repositories on every plan, so add yourself.
   The workflow's own gate also applies: only a run that you start by hand
   can submit.
3. **Environment secrets**: add the four secrets from 6.1.
4. **Environment variables**: add `WINDOWS_STORE_FLIGHT_ID`, the ID of your
   tester flight. `msstore flights list <Store ID>` shows it
   ([5.3](#53-tester-builds-after-the-first-release)). Without it, tester
   builds go to the private audience instead.

Then, under **Settings** > **Secrets and variables** > **Actions** >
**Variables**, add two repository variables:

| Repository variable | Value |
| --- | --- |
| `WINDOWS_STORE_ID` | `9NMZ345ZJ6JK`, the Store ID from Product identity |
| `STORE_SUBMISSION_ENABLED` | `true`, only after your first submission is published |

Paste secrets only into GitHub's secret fields, never into chat, issues or
files. From a terminal, `gh secret set PARTNER_CENTER_CLIENT_SECRET --env
windows-store --repo dorlugasigal/annotator` asks for the value without
showing it.

### 6.3 Send a build

1. Approve and prepare the candidate as in [5.2](#52-the-first-submission-step-by-step),
   with the Partner Center values committed. Run the owner-run publisher
   first, for its local validation and WACK.
2. On GitHub, open **Actions** > **Release** > **Run workflow** and choose
   branch `main`.
3. Fill in **version** (the approved label, such as `0.1.0-beta.8`) and
   **candidate_sha** (the full SHA of the newest commit on `main`).
4. Set **windows_store**:
   - `testers`: the package flight in `WINDOWS_STORE_FLIGHT_ID`. Without a
     flight, the main submission, but only while its audience is private.
     Otherwise the job stops and leaves a draft for you to check.
   - `public`: the main submission as it is. Choose it only after your launch
     decision ([5.4](#54-the-launch-decision)).
5. Leave **notarize** and **publish** off, unless the same run also releases
   the Mac.
6. Select **Run workflow**. The run checks the candidate on Linux, builds and
   checks the bundle on Windows, then sends it. The run's summary shows the
   bundle's SHA-256 and where it went.
7. In Partner Center, check the submission. Certification can take up to
   three business days.

If the Store job fails, open the run and select **Re-run failed jobs**. It
sends the same bundle again; `msstore publish` replaces a draft that is
waiting. The CLI version and its SHA-256 are pinned in
`tools/windows/toolchain.json` (`msstoreCli`); update both together.

## Still open

- The Release workflow's Windows jobs haven't run on GitHub yet. A local dry
  run of the build steps passed (acceptance record); the hosted build, the
  Store job and its Partner Center sign-in are owner checks for the first
  manual run.
- The website's "Microsoft Store and Windows" privacy section is live since
  `storeId` was set on 2026-10-21. Before that, the privacy policy link opened
  a page that described Annotator for Mac; certification passed anyway.
- Screenshots: the three uploaded ones are live; scenes 4 and 5 are optional
  ([4](#4-screenshots)).
- Done in a Windows 11 on Arm VM on the owner's Mac (2026-10-21): WACK, the
  ARM64 and x64 packages, and the public Store package installed with winget
  (ARM64, signature kind Store) and started. Still not done on a physical PC:
  an x64 PC and an ARM64 PC. These and the other physical checks are listed in
  the acceptance record.
- The bundle has no symbol file, so Partner Center's crash reports won't have
  Annotator's function names. An `.msixupload` file with symbols would add
  them later.
- Microsoft doesn't document the formats of the publisher value or the Store
  ID. `--check-store` checks only what the package schema and the website
  require.
- Microsoft Store Policies 7.20 take effect on 2026-10-22; read the changes
  before you submit.
- Testers need a Windows test card, like `beta-testing.md` for the Mac.

## Sources

Microsoft Learn, read on 2026-10-20:

- [Open a developer account](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account)
- [Reserve your app's name](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/reserve-your-apps-name) and [manage app names](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/msix/manage-app-name-reservations)
- [View app identity details](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)
- [Create an app submission](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission), [visibility options](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/visibility-options) and [pricing and availability](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/price-and-availability)
- [App properties](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/enter-app-properties), [categories](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/categories-and-subcategories), [support info](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/support-info) and [product declarations](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/product-declarations)
- [Age ratings](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/age-ratings)
- [Upload app packages](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages) and [app package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)
- [Store listings](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info) and [screenshots and images](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
- [Submission options](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options) and [the certification process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-certification-process)
- [Package flights](https://learn.microsoft.com/en-us/windows/apps/publish/package-flights) and [customer groups](https://learn.microsoft.com/en-us/windows/apps/publish/create-customer-groups)
- [App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- [Microsoft Store Developer CLI](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/overview) and [its commands](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands); the CLI's source (`microsoft/msstore-cli`, `MSIXProjectPublisher`) also takes a `.msixbundle` path
- [Microsoft Store Policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies) (version 7.20; 10.1.3 search terms, 10.5.1 privacy policy, 10.6 capabilities, 10.8 purchases)
