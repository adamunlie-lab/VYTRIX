# Regenerates docs/terms/index.html (the public Terms page on GitHub Pages) from legal/TERMS.txt.
# Run it whenever the Terms change, then commit and push. ASCII only.
$root = Split-Path $PSScriptRoot -Parent
$terms = [IO.File]::ReadAllText((Join-Path $root 'legal\TERMS.txt'), [Text.Encoding]::UTF8)
$escaped = $terms.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
$html = @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>VYTRIX Terms of Agreement</title>
<link rel="stylesheet" href="../site.css">
</head>
<body>
<main class="page">
  <a class="brand" href="../"><span class="mark">V</span>VYTRIX</a>
  <h1>Terms of Agreement</h1>
  <p class="dim">The same Terms VYTRIX shows when you install and start it. Privacy is covered in section 6; there's also a <a href="../privacy/">plain-language privacy summary</a>.</p>
<pre>
$escaped
</pre>
  <p class="foot"><a href="../">Home</a> &middot; <a href="../privacy/">Privacy policy</a></p>
</main>
</body>
</html>
"@
$out = Join-Path $root 'docs\terms'
New-Item -ItemType Directory -Force $out | Out-Null
[IO.File]::WriteAllText((Join-Path $out 'index.html'), $html, (New-Object Text.UTF8Encoding $false))
"Wrote docs/terms/index.html"
