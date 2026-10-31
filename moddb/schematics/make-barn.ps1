# Builds a simple open-front wooden barn as a WorldEdit schematic (livestock-barn.json).
# Layout: 11 x 9 blocks including a 1 block roof overhang; log posts, plank walls on three sides,
# open front (south side, z = 1), a shallow gable roof of plank slabs, a few hay bales inside.
param([string]$out)
$codes = [ordered]@{}
$blocks = @{}
function Id($code) { if (-not $codes.Contains($code)) { $codes[$code] = $codes.Count + 1 }; return $codes[$code] }
function Put($x, $y, $z, $code) { $blocks["$x,$y,$z"] = Id $code }

$log = "game:log-placed-oak-ud"; $plank = "game:planks-oak-ud"
$slabLow = "game:plankslab-oak-down-free"; $slabHigh = "game:plankslab-oak-up-free"; $hay = "game:hay-normal-ud"

# Posts: corners and the middle of the back wall, 4 blocks high.
foreach ($p in @(@(1,1),@(9,1),@(1,7),@(9,7),@(5,7),@(5,1))) { for ($y = 0; $y -le 3; $y++) { Put $p[0] $y $p[1] $log } }
# Back wall (north, z = 7) and side walls (x = 1, x = 9).
for ($y = 0; $y -le 3; $y++) {
  for ($x = 2; $x -le 8; $x++) { if ($x -ne 5) { Put $x $y 7 $plank } }
  for ($z = 2; $z -le 6; $z++) { Put 1 $y $z $plank; Put 9 $y $z $plank }
}
# Roof: ridge along x at z = 4, rising half a block per block from the eaves.
for ($x = 0; $x -le 10; $x++) {
  for ($z = 0; $z -le 8; $z++) {
    $d = [Math]::Abs($z - 4)           # 0 at the ridge, 4 at the eaves
    switch ($d) {
      4 { Put $x 4 $z $slabLow }
      3 { Put $x 4 $z $slabHigh }
      2 { Put $x 5 $z $slabLow }
      1 { Put $x 5 $z $slabHigh }
      0 { Put $x 5 $z $plank; Put $x 6 $z $slabLow }
    }
  }
}
# Beams on top of the front and back walls, so the roof meets them without a gap.
for ($x = 1; $x -le 9; $x++) { Put $x 4 1 $plank; Put $x 4 7 $plank }
# Gable ends: fill the triangles above the side walls.
foreach ($x in 1, 9) {
  for ($z = 2; $z -le 6; $z++) { Put $x 4 $z $plank }
  for ($z = 3; $z -le 5; $z++) { Put $x 5 $z $plank }
}
# A few hay bales along the back wall.
Put 3 0 6 $hay; Put 4 0 6 $hay; Put 3 1 6 $hay; Put 7 0 6 $hay

$indices = New-Object System.Collections.Generic.List[long]
$ids = New-Object System.Collections.Generic.List[int]
foreach ($key in $blocks.Keys) {
  $c = $key.Split(','); $x = [int]$c[0]; $y = [int]$c[1]; $z = [int]$c[2]
  $indices.Add(([long]$y -shl 20) -bor ([long]$z -shl 10) -bor $x)   # same packing as vanilla schematics
  $ids.Add($blocks[$key])
}
$blockCodes = [ordered]@{}; foreach ($k in $codes.Keys) { $blockCodes["$($codes[$k])"] = $k }
$schematic = [ordered]@{
  GameVersion = "1.22.7"; SizeX = 11; SizeY = 7; SizeZ = 9
  BlockCodes = $blockCodes; ItemCodes = @{}; Indices = $indices; BlockIds = $ids
  DecorIndices = @(); DecorIds = @(); BlockEntities = @{}; Entities = @()
  ReplaceMode = 2; EntranceRotation = -1
}
[System.IO.File]::WriteAllText($out, ($schematic | ConvertTo-Json -Depth 5 -Compress), (New-Object System.Text.UTF8Encoding $false))
"{0} blocks, {1} block types" -f $ids.Count, $codes.Count
