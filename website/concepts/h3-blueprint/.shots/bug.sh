#!/bin/bash
# The reported steps: load, wait, one press on the stage, Control-2 twice, Esc, Control-Shift-2, resize there and back.
W=$1; H=$2; W2=$3; H2=$4; T=${5:-dark}
P="playwright-cli -s=bp3r4"; S="$(cat .shots/st.js)"
st() { echo "  $1: $($P eval "$S" 2>&1 | sed -n 2p | sed 's/\\"/"/g')"; }
$P resize $W $H >/dev/null 2>&1
$P goto "http://127.0.0.1:4310/concepts/h3-blueprint/?theme=$T" >/dev/null 2>&1
sleep 11
echo "$W x $H $T, resize to $W2 x $H2 and back"
XY=$($P eval "(() => { const b = document.querySelector('#screen').getBoundingClientRect(); return Math.round(b.left + b.width / 2) + ' ' + Math.round(Math.min(innerHeight - 30, b.top + 60)); })()" 2>&1 | sed -n 2p | tr -d '"')
$P mousemove $XY >/dev/null 2>&1; $P mousedown >/dev/null 2>&1; $P mouseup >/dev/null 2>&1
sleep 3
st "grown after one press"
for i in 1 2; do $P keydown Control >/dev/null 2>&1; $P press 2 >/dev/null 2>&1; $P keyup Control >/dev/null 2>&1; sleep 1.4; done
$P press Escape >/dev/null 2>&1; sleep .5
$P keydown Control >/dev/null 2>&1; $P keydown Shift >/dev/null 2>&1; $P press 2 >/dev/null 2>&1; $P keyup Shift >/dev/null 2>&1; $P keyup Control >/dev/null 2>&1; sleep .6
st "Control-2 twice, Esc, Control-Shift-2"
$P resize $W2 $H2 >/dev/null 2>&1; sleep 2.2
st "resized"
$P screenshot --filename=.shots/bug-$W-resized.png >/dev/null 2>&1
$P resize $W $H >/dev/null 2>&1; sleep 2.2
st "back at the first size"
$P screenshot --filename=.shots/bug-$W-back.png >/dev/null 2>&1
echo "  console: $($P console 2>&1 | sed -n 2p)"
