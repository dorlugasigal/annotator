# Let an AI assistant use Annotator

An AI app with computer-use tools can click Annotator's buttons, draw shapes,
type labels and move them. It uses the same app you use.

Your AI app may send screenshots and text to its model provider. Check that
app's privacy settings before using it with private information. Its
computer-use tools need their own macOS permissions.

In **Settings > Help**, expand **Use an AI assistant** and choose
**Save AI skill...**. It saves an
`annotator` folder containing the instructions. The website's **AI skill** page
also has a ZIP download and installation steps.

For Copilot CLI, move that folder into `~/.copilot/skills/`, then run
`/skills reload` and `/skills info annotator`. You can also use
`/skills add /path/to/annotator` with the folder you saved.

For Claude Code, put the folder in `~/.claude/skills/` and start a new session.
Other AI apps may use a different skills folder.

Installing the skill does not add computer-use tools or grant macOS permissions.
Then ask for what you want:

> Use Annotator to circle the wrong date and add "Check this date".

The skill tells the AI to check the screen, choose a tool, make the change and
check the result. It does not stop to ask before every shape. It keeps your
existing work and asks before doing anything outside your request, such as
deleting old work or sharing a file.

There is no AI switch in Annotator. You do not need a drawing file, a separate
Annotator server or Node.js for this approach. Your AI app needs working
computer-use tools and the macOS permissions those tools require.

All current tools are free. Annotator's usual permissions still apply. For screen zoom,
allow Screen Recording. To use Option-scroll over other apps, also allow
Accessibility. The AI can use the zoom buttons instead.
Control-scroll stays available for macOS's own zoom.

For a longer explanation, try:

> Draw a small system-design interview: a user, a Link API, a database and a
> cache. Add a title and readable labels, connect the parts, use two colors and
> a light fill, then highlight the read path. Keep my existing drawings.

The skill explains the Shapes library, separate start/end arrowheads, colors,
fills and roughness. For requested demos it adds pauses so viewers can read
the result. A provider that supports only endpoint drags cannot draw a custom
freehand path or control its speed. Installing this skill does not remove that
provider limit.

The product film uses a native test driver for repeatable pen movement and
safe capture. It is not proof that an external AI provider followed every step.
Computer-use checks and native recording checks are reported separately.

Tools is a resizable native window, not a fixed popover. Widen it for more
columns or scroll the grid while the categories and footer remain fixed.
Double-click inside a filled or unfilled shape to edit its centered owner label.
The label stays with the shape through movement, resizing, rotation and saving.
Web uses a globe; API gateway and Load balancer are separate entries. The
palette and canvas use the same system-symbol paths.
