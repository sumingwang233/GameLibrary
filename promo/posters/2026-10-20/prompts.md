# GameLibrary 海报生成提示词

生成方式：内置 `image_gen`，每张独立生成。输入官方 Logo。四张最终图片均为概念插画，不展示生成的软件界面。第一张先用现有截图探索排版，再按文末修订提示词替换为纸雕插画，截图初稿不作为交付。

实际输出：4 张 PNG，均为 1024 × 1536 像素，2:3 竖版。提示词中的目标尺寸未被工具采用，交付尺寸以实际 PNG 为准。

## 01-editorial-library.png（初稿排版；最终修改见文末）

参考图：
- `D:\Official\GameLibrary\website\public\assets\icon.png`
- `D:\Official\GameLibrary\website\public\assets\library.png`

```text
Use case: ads-marketing.
Create ONE finished, exceptionally art-directed promotional poster for the real Windows application GameLibrary. Portrait aspect ratio 2:3, aim for 1536 x 2304 pixels or higher at same ratio, with generous safe margins and impeccable Simplified Chinese typography. This is a final designed image, not a mockup of a poster in a room. Branding: use the attached official colorful FLAT gamepad-and-cards app icon exactly, preserve its silhouette and palette, no rebranding, no 3D/glossy restyling of the logo. The wordmark must read exactly "GameLibrary", with that capitalization.
Audience: Windows players who keep independent/local games in folders.
All poster copy must be clean, natural, legible Simplified Chinese, typeset with professional hierarchy and deliberate line breaks. Every listed string must be rendered correctly; do not invent extra product claims, prices, ratings, downloads, QR codes, store badges, platform logos or endorsements. No macOS controls, no mobile version, no cloud sync, no automatic translation of all games. No famous game characters or copyrighted game cover artwork. The final artwork must look suitable for a designer's campaign portfolio, with a distinctive composition and polished detail, not a generic landing-page card grid. Do not print prompt instructions or a style description. Opaque background.
Input images: Image 1 is the official icon. Image 2 is an EXISTING REAL application screenshot, version 1.5.5 Simplified Chinese isolated demo library, 1320:820 aspect ratio. Use Image 2 as a faithful intact rectangular photograph/insert, as close to pixel-identical as possible; do not redesign, relabel, translate, improve covers, invent cards, add UI controls or recolor it. It is a dark Windows app screenshot. No other application screenshots.
Style/medium: A premium minimalist Swiss editorial product advertisement, warm ivory paper, almost-black typography, very restrained teal editorial rules picking up one color in the official icon. Refined typography, outstanding negative space, subtle fine paper texture. This should feel like a cultured printed design publication, not a tech neon ad.
Composition: Small official icon and GameLibrary wordmark at the upper left, tiny "WINDOWS LOCAL GAME LIBRARY" aligned opposite. The main headline occupies the upper third in very large bold elegant Chinese type, left aligned. Beneath it place a concise subtitle. The unwarped complete real screenshot is the striking horizontal anchor across the middle/lower portion, on a quiet thin keyline, not on a fabricated monitor or tilted laptop. Keep the screenshot recognizable, preserve the full app header and sidebar. Include the actual version caption below it. At the bottom use an elegant 3-column typographic feature strip with 01/02/03 and a very small footer. Strong editorial spacing and print precision.
Text (verbatim):
"GameLibrary"
"WINDOWS LOCAL GAME LIBRARY"
"游戏散在各处。"
"在这里，一起管理。"
"把硬盘里的游戏，放进同一个游戏库。"
"扫描指定目录"
"入库前逐项确认"
"无需账号，离线管理"
"界面参考：v1.5.5 · 简体中文 · 示例库"
"Windows 10 / 11 x64"
"扫描与移出记录保留原始游戏文件"
"github.com/sumingwang233/GameLibrary"
Constraints: Do not print any other version or the word latest. The screenshot caption must be readable but subordinate. No generic 3D floating cubes, no neon, no phone. The headline is the hero; screenshot is evidence.
```

## 02-pixel-scan-review-launch.png

参考图：
- `D:\Official\GameLibrary\website\public\assets\icon.png`

```text
Use case: ads-marketing.
Create ONE finished, exceptionally art-directed promotional poster for the real Windows application GameLibrary. Portrait aspect ratio 2:3, aim for 1536 x 2304 pixels or higher at same ratio, with generous safe margins and impeccable Simplified Chinese typography. This is a final designed image, not a mockup of a poster in a room. Branding: use the attached official colorful FLAT gamepad-and-cards app icon exactly, preserve its silhouette and palette, no rebranding, no 3D/glossy restyling of the logo. The wordmark must read exactly "GameLibrary", with that capitalization.
Audience: Windows players who keep independent/local games in folders.
All poster copy must be clean, natural, legible Simplified Chinese, typeset with professional hierarchy and deliberate line breaks. Every listed string must be rendered correctly; do not invent extra product claims, prices, ratings, downloads, QR codes, store badges, platform logos or endorsements. No macOS controls, no mobile version, no cloud sync, no automatic translation of all games. No famous game characters or copyrighted game cover artwork. The final artwork must look suitable for a designer's campaign portfolio, with a distinctive composition and polished detail, not a generic landing-page card grid. Do not print prompt instructions or a style description. Opaque background.
Input image: Official app icon, keep it small and flat in a discrete brand lockup at top. This is illustration, NOT a literal screenshot and NOT a game made by GameLibrary.
Style/medium: Spectacular crafted retro 16-bit pixel-art adventure poster with a 1990s Japanese game magazine energy, printed halftone details only in typography zone, sharp deliberate pixel edges in the scene, warm pale peach paper, forest mint, warm coral, ochre and deep ink violet. Absolutely no neon glow, no cyberpunk, no huge lens flares. The pixel illustration should be rich and beautiful, hand-composed and original, never generic AI clipart.
Composition: The headline sits very large at top in powerful clean Chinese display type, paired with an understated pixel-stencil English kicker. The middle 55 percent is a storybook isometric desktop landscape: a few scattered physical folder-shaped islands on the left connect via a crisp dotted path to a neat library shelf at center and a joyful gamepad-shaped launch portal on the right. Show three steps on the path with clearly readable labels "扫描", "确认", "启动". A small original cursor-shaped traveler and tiny original pixel trees, original star tokens and folder tabs form a playful metaphor for finding local games. Game cartridges/covers are generic original geometric pictures, no IP characters, no made-up application interface. Layering and scale must be carefully art directed with a hero focal point, plenty of breathing room for copy. Bottom is a printed editorial feature panel, not UI cards.
Text (verbatim):
"GameLibrary"
"LOCAL GAMES, READY TO PLAY."
"从文件夹，到开玩。"
"扫描"
"确认"
"启动"
"扫描指定目录，确认后加入游戏库。"
"保存启动文件与参数"
"常玩的游戏，点进库里就能找。"
"扫描与移出记录保留原始游戏文件"
"Windows 10 / 11 x64"
"github.com/sumingwang233/GameLibrary"
Constraints: Spell every Chinese word correctly. Illustration is metaphorical, no fake screenshot, no pixelated illegible Chinese. Keep main Chinese text sharp professionally typeset while illustration remains pixel art. No false instant-start success dialogs.
```

## 03-blueprint-unity-translation.png

参考图：
- `D:\Official\GameLibrary\website\public\assets\icon.png`

```text
Use case: ads-marketing.
Create ONE finished, exceptionally art-directed promotional poster for the real Windows application GameLibrary. Portrait aspect ratio 2:3, aim for 1536 x 2304 pixels or higher at same ratio, with generous safe margins and impeccable Simplified Chinese typography. This is a final designed image, not a mockup of a poster in a room. Branding: use the attached official colorful FLAT gamepad-and-cards app icon exactly, preserve its silhouette and palette, no rebranding, no 3D/glossy restyling of the logo. The wordmark must read exactly "GameLibrary", with that capitalization.
Audience: Windows players who keep independent/local games in folders.
All poster copy must be clean, natural, legible Simplified Chinese, typeset with professional hierarchy and deliberate line breaks. Every listed string must be rendered correctly; do not invent extra product claims, prices, ratings, downloads, QR codes, store badges, platform logos or endorsements. No macOS controls, no mobile version, no cloud sync, no automatic translation of all games. No famous game characters or copyrighted game cover artwork. The final artwork must look suitable for a designer's campaign portfolio, with a distinctive composition and polished detail, not a generic landing-page card grid. Do not print prompt instructions or a style description. Opaque background.
Input image: official flat icon as a small brand signature.
Style/medium: An exquisite technical blueprint / industrial schematic campaign poster. Ink navy blueprint paper, cool off-white engineering linework, desaturated cyan, one restrained amber-orange accent plate. Very fine grid, small coordinate ticks, measurement leaders, elegant mechanical drawing conventions, subtle screenprint texture. Highly precise orthographic drafting with a few thin axonometric construction lines; no glowing sci-fi, no CGI cubes, no actual UI.
Composition: Strong asymmetric grid. Wordmark and icon at top; a compact English engineering kicker. Oversized Chinese headline in the upper third. Central hero is a beautiful original exploded schematic of one local game file capsule, a translation plugin module and a Chinese font glyph plate bearing a huge clean "中", with clear mechanical connectors suggesting configuration and recovery. An amber bracket returns from the plugin layer to the original capsule to convey backup/rollback. The drawings are conceptual technical illustration, not screenshots and not a visual demo of actual in-game translation. Use ample empty navy space, disciplined labels, beautiful subtle drafting detail and high contrast. Three practical supporting claims form the lower band; an important prerequisite sentence must remain plainly readable just above the footer.
Text (verbatim):
"GameLibrary"
"UNITY TRANSLATION SETUP"
"Unity 游戏，"
"配好中文再开玩。"
"为受支持的 Unity 游戏配置翻译插件"
"中"
"先备份"
"可回滚"
"补中文字体"
"需自行配置翻译服务，实际效果因游戏而异。"
"Windows 10 / 11 x64"
"github.com/sumingwang233/GameLibrary"
Constraints: Do not use Unity logo or a fake translation screenshot; "Unity" is plain typeset descriptive text. No claim that all games work or that translation is free/offline. Do not claim in-game translation quality has been verified. No new product functionality, no Godot/MTool branding, no API keys, no credentials. This is a concept poster about configuration.
```

## 04-archive-covers-tags.png

参考图：
- `D:\Official\GameLibrary\website\public\assets\icon.png`

```text
Use case: ads-marketing.
Create ONE finished, exceptionally art-directed promotional poster for the real Windows application GameLibrary. Portrait aspect ratio 2:3, aim for 1536 x 2304 pixels or higher at same ratio, with generous safe margins and impeccable Simplified Chinese typography. This is a final designed image, not a mockup of a poster in a room. Branding: use the attached official colorful FLAT gamepad-and-cards app icon exactly, preserve its silhouette and palette, no rebranding, no 3D/glossy restyling of the logo. The wordmark must read exactly "GameLibrary", with that capitalization.
Audience: Windows players who keep independent/local games in folders.
All poster copy must be clean, natural, legible Simplified Chinese, typeset with professional hierarchy and deliberate line breaks. Every listed string must be rendered correctly; do not invent extra product claims, prices, ratings, downloads, QR codes, store badges, platform logos or endorsements. No macOS controls, no mobile version, no cloud sync, no automatic translation of all games. No famous game characters or copyrighted game cover artwork. The final artwork must look suitable for a designer's campaign portfolio, with a distinctive composition and polished detail, not a generic landing-page card grid. Do not print prompt instructions or a style description. Opaque background.
Input image: official colorful flat icon, exact brand lockup, small near top.
Style/medium: A stunning independent art-book / collector's archive / analog mixed-media collage poster. Warm fibrous parchment stock, deep bottle-green, vermilion and ink-black accents. Tactile cut-paper edges, exquisite woodcut/risograph ORIGINAL cover illustrations, archival index tabs, subtle dry-brush registration texture. Warm, human and cultured. This must be dramatically different from the minimalist white poster, pixel-art poster and navy engineering poster.
Composition: Large handsome expressive Chinese typography like a contemporary museum exhibition, plenty of negative space at top, then a confident diagonally layered still-life of original illustrated game-cover-sized paper cards, library catalog slips, one tiny star bookmark and die-cut tag labels. The fictional cover artworks are purely conceptual, ORIGINAL decorative cover art, not real games/screenshots. One cover has a calm winter landscape, another a small abstract forest, another a simple dawn journey; no famous IP, characters or screenshots. The paper tag labels read actual supported user-created category examples "周末想玩", "轻松一刻", "故事与冒险", elegantly integrated, not generic software pill cards. A thin dark-green archival rule anchors practical feature copy and footer. The hierarchy and material craft should feel expensive and collectible. Logo stays flat and unchanged; texture only belongs to the poster around it.
Text (verbatim):
"GameLibrary"
"THE PERSONAL GAME ARCHIVE"
"你的游戏，"
"有自己的分类。"
"封面自己选，标签自己定。"
"周末想玩"
"轻松一刻"
"故事与冒险"
"自定义封面"
"标签筛选"
"收藏与搜索"
"整理本地游戏，无需账号。"
"Windows 10 / 11 x64"
"插画为视觉示意"
"github.com/sumingwang233/GameLibrary"
Constraints: No fabricated app UI, no collectible NFT marketplace, no cloud, no imaginary game promotion. This promotes a local game library organizer. No photographs of a room, no people, no phone, no watermark. Rich tactile visual craft, not random scrapbook clutter.
```

## 01-editorial-library.png 最终修订

输入：第一张生成的截图排版初稿，以及官方 Logo。最终稿以纸雕插画替换截图区域，并将图注改为“插画为视觉示意”。

```text
Use case: compositing. Edit this existing finished portrait GameLibrary poster, keeping its excellent warm ivory Swiss editorial typography, brand lockup, large Chinese headline, feature strip, spacing, paper texture, footer, and all other printed text EXACTLY unchanged. This is a final opaque flat poster, 2:3 portrait, keep resolution at least the input image's.
Input image 1: existing poster to edit. Input image 2: the official colorful flat logo; it is an identity reference only, keep its visual identity unchanged.
CHANGE ONLY THE LARGE CENTRAL SCREENSHOT REGION AND ITS SMALL CAPTION. Remove every trace of the application screenshot, window controls, interface, sidebar, search bar, dark software-panel rectangle, cyan screen border, and any tiny fake UI. Replace this entire central image region with a spectacular art-directed physical paper-sculpture / editorial illustration in a softly lit ivory studio matching the background: an elegant low open archival folder with a few ORIGINAL colorful game-cover art cards arranged into one orderly local library. Restrained teal, orange, sage and coral accents echo the official logo. Cards show original abstract winter, forest, starfield and journey illustrations, absolutely no UI, no recognizable copyrighted games or characters, no text on covers. Warm matte tactile surfaces, subtle realistic grounding shadows, absolutely no neon or shiny plastic, no giant app icon. This picture metaphorically shows locally stored games gathered into one folder, not a feature screenshot. It fills the same central space elegantly, with depth and inviting craft. Do not make a card-grid interface. No monitor, laptop, phone, room or human.
Replace the old caption "界面参考：v1.5.5 · 简体中文 · 示例库" with the accurate small caption "插画为视觉示意". Keep this caption subordinate and readable.
All invariant text must remain exactly:
"GameLibrary"
"WINDOWS LOCAL GAME LIBRARY"
"游戏散在各处。"
"在这里，一起管理。"
"把硬盘里的游戏，放进同一个游戏库。"
"01"
"02"
"03"
"扫描指定目录"
"入库前逐项确认"
"无需账号，离线管理"
"Windows 10 / 11 x64"
"扫描与移出记录保留原始游戏文件"
"github.com/sumingwang233/GameLibrary"
Only edit the middle illustration area and its caption; preserve all other type and layout.
```


