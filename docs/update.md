# 游戏内公告与更新

[version.json](../version.json) 中的 `notice` 用于玩家公告，`release` 用于完整包的版本提示。公告可以使用现有的镜像。差分更新由官方 GitHub 仓库的 `delta` 字段启用，可与公告出现在同一个弹窗。

## 公告

| 字段 | 用途 |
|:--|:--|
| `version` | 公告编号。有新内容时换一个编号。 |
| `updated` | 可选的公告日期。 |
| `title` / `title_en` | 中文／英文标题。 |
| `message` / `message_en` | 中文／英文正文。 |
| `buttons` | 可选链接按钮，每项包含 `text`、可选的 `text_en` 和 `url`。 |
| `image` / `images` | 可选的单张或多帧图片。多帧以 `fps` 播放，默认 8。 |

`release.latest` 高于客户端 Rain Meadow 版本时显示完整包提示。可填写 `title`、`notes` 和下载地址 `download`。差分检查使用完整包的文件哈希识别基线，同一个 Rain Meadow 版本号的多次 Android 发布也可以更新。

## 差分更新

CI 按文件内容比较上一份已公开发布、且游戏快照相同的完整 ZIP。差分 ZIP 包含新增和有改动的文件。清单列出基线与目标版本的文件哈希，安装时以此识别应删除的旧文件。两个版本相同或找不到公开的基线时，CI 只生成完整包草稿。

游戏在菜单中检查官方清单。匹配基线时，玩家可选择下载差分，再选择「安装并退出」。校验或安装失败时可以打开目标完整包页面。不能匹配的安装，例如改过 DLL 的调试包，也使用完整包。模组目录中未列入正式包的额外文件会保留。

游戏校验清单、差分 ZIP、待写入的文件和本地基线的 SHA-256。安装前备份受影响的文件，最后替换主 DLL。安装中断时，下次启动尝试恢复旧文件或确认目标文件已全部安装。使用旧版更新器的玩家需要先手动安装一份支持差分的完整包。

## 发布流程

1. 按 [CI 发布流程](ci.md#github-release) 创建正式构建。完整 ZIP 和 SHA-256 仍在原来的 Release 草稿里。存在差分时，CI 另外创建 `<目标标签>-delta-<游戏快照>` 草稿。
2. 检查并发布完整 Release。再发布差分 Release，保留其预发行标记。差分 Release 包含 ZIP、哈希清单和 `RainMeadow-android-update.json`。
3. 将 `RainMeadow-android-update.json` 中的顶层 `delta` 字段合入主分支的 `version.json`。公告可独立更新。

## 本地验证

```sh
python3 -IB tools/rain-meadow/delta.py create work/base.zip work/target.zip \
  work/delta 1.10 base-tag target-tag
python3 -IB tools/rain-meadow/delta.py verify work/base.zip \
  work/delta/RainMeadow-android-delta.zip work/delta/RainMeadow-android-delta.json work/target.zip
```

校验过程按清单重建目标文件集合，逐个核对内容。大小写重命名和文件与目录互换时使用完整 ZIP。
