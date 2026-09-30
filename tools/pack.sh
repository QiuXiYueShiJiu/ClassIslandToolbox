#!/usr/bin/env bash
#
# 把插件打成可以直接在 ClassIsland 里安装的 .cipx 包。
#
#   ./pack.sh
#
# 产物：dist/Toolbox-<manifest.yml 里的 version>.cipx
#
# .cipx 就是一个 zip，manifest.yml 和插件 dll 放在压缩包根目录。
# 由宿主提供的程序集（ClassIsland.Core、Avalonia、FluentAvalonia……）一律不打进去，
# 否则插件加载上下文里会出现第二份，宿主自己那份的类型就对不上了。
set -euo pipefail

# 本脚本在 tools/ 下，插件工程在 ../src/ClassIsland.Toolbox
cd "$(dirname "$0")/../src/ClassIsland.Toolbox"
ROOT="$PWD"

# 有些 CI / 容器环境里会带一个名为 version 的环境变量（值可能是 N/A），
# MSBuild 会把它当成属性 $(Version)，还原时会报
# "'N/A' is not a valid version string"。项目文件里虽然写了 <Version> 能盖住它，
# 这里还是先清掉，免得踩到别的默认属性。
unset version 2>/dev/null || true

CONFIG="${CONFIG:-Release}"

VERSION="$(sed -n 's/^version:[[:space:]]*//p' manifest.yml | head -1 | tr -d '\r' | xargs)"
if [ -z "$VERSION" ]; then
    echo "错误：manifest.yml 里读不到 version" >&2
    exit 1
fi

echo "==> 编译（$CONFIG，版本 $VERSION）"
dotnet build -c "$CONFIG" --nologo -v m

OUT="bin/$CONFIG/net8.0"
if [ ! -f "$OUT/ClassIsland.Toolbox.dll" ]; then
    echo "错误：没有找到编译产物 $OUT/ClassIsland.Toolbox.dll" >&2
    exit 1
fi

STAGE="dist/.stage-$VERSION"
rm -rf "$STAGE"
mkdir -p "$STAGE"

copy() {
    local name="$1"
    if [ ! -f "$OUT/$name" ]; then
        echo "错误：缺少要打包的文件 $OUT/$name" >&2
        exit 1
    fi
    cp -f "$OUT/$name" "$STAGE/$name"
    echo "    + $name"
}

echo "==> 收集插件文件"
copy ClassIsland.Toolbox.dll
copy manifest.yml
if [ -f "$OUT/README.md" ]; then copy README.md; fi
if [ -f "$OUT/icon.png" ]; then copy icon.png; fi

# pack-include.txt 里列的是「宿主没有、必须随插件自带」的东西，
# 主要是拍照抽人用的 ONNX Runtime 和 YuNet 模型。
while IFS= read -r line || [ -n "$line" ]; do
    line="${line%%#*}"
    line="$(printf '%s' "$line" | xargs)"
    if [ -n "$line" ]; then
        copy "$line"
    fi
done < pack-include.txt

# 兜底检查：这些必须由宿主提供，出现了就说明 csproj 的 Private/ExcludeAssets 写错了。
for banned in ClassIsland.Core.dll ClassIsland.Shared.dll ClassIsland.Platforms.Abstractions.dll \
              Avalonia.dll Avalonia.Base.dll FluentAvalonia.dll; do
    if [ -f "$STAGE/$banned" ]; then
        echo "错误：$banned 应该由宿主提供，不能打进插件包" >&2
        exit 1
    fi
done

CIPX="dist/Toolbox-$VERSION.cipx"
rm -f "$CIPX"

echo "==> 打包 $CIPX"
( cd "$STAGE" && zip -q -X -r "$ROOT/$CIPX" . )
rm -rf "$STAGE"

echo
echo "==> 完成：$CIPX（$(du -h "$CIPX" | cut -f1)）"
unzip -l "$CIPX"
