# 战争雷霆游戏更新接口文档

> 适用范围：PTL（Pain Thunder Launcher）直接检查和下载战争雷霆游戏更新。
>
> 本文描述的是战争雷霆官方启动组件当前使用的 HTTP、YUP 和 P2P 接口，不是 Gaijin 对外承诺的稳定公共 API。端点、字段和下载地址均可能随官方更新而改变。
>
> 实测日期：2026-11-06（Asia/Shanghai）
>
> 对应实现：
> [`ModWarThunder.vb`](../PTL/Modules/ModWarThunder.vb)、
> [`ModWarThunderSelfUpdate.vb`](../PTL/Modules/ModWarThunderSelfUpdate.vb)、
> [`ModWarThunderUpdate.vb`](../PTL/Modules/ModWarThunderUpdate.vb)

---

## 1. 速览

| 用途 | 方法 | 地址 | 响应 |
|---|---|---|---|
| 查询线上游戏版本 | `GET` | `https://yupmaster.gaijinent.com/yuitem/get_version.php` | `text/plain`，四段版本号 |
| 获取更新清单 | `GET` | `https://yupmaster.gaijinent.com/yuitem/get_yup.php` | `302` 后返回 `.yup` 文件 |
| 获取 CDN 和 tracker 配置 | `GET` | `https://yupmaster.gaijinent.com/launcher/cdn_conf.php` | JSON |
| P2P 主 tracker | UDP | `udp://yuptracker.gaijinent.com:27022/announce` | BEP 15 |
| P2P 备用 tracker | UDP | `udp://yuptracker-sa.gaijinent.com:27029/announce` | BEP 15 |
| P2P 备用 tracker | UDP | `udp://yuptracker-sa.gaijinent.com:27022/announce` | BEP 15 |

核心结论：

1. 正式服请求版本接口时，`tag` 应传空字符串；测试服传 `dev`。
2. `get_yup.php` 返回的是 BitTorrent 元信息与战争雷霆 YUP 扩展字段组合而成的 Bencode 文件。
3. `.yup` 中的 `info` 字典同时承担 Torrent 元信息作用，游戏文件可以通过 HTTP CDN 或 P2P 下载。
4. 更新前应同时校验文件长度和 `sha1`，安装完成后需要把新清单原子替换到游戏目录的 `warthunder.yup`。
5. 所有接口当前均未使用账号令牌或 API Key。

---

## 2. 服务器通道与版本查询

### 2.1 请求

```http
GET /yuitem/get_version.php?proj=warthunder&tag= HTTP/1.1
Host: yupmaster.gaijinent.com
User-Agent: PTL/<launcher-version>
```

参数：

| 参数 | 必填 | 说明 |
|---|---|---|
| `proj` | 是 | 固定为 `warthunder` |
| `tag` | 是，但可为空 | 正式服为空字符串；测试服为 `dev` |

PTL 中的通道映射：

| PTL 设置 | 请求的 `tag` |
|---|---|
| 正式服、`production` | 空字符串 |
| 测试服 | `dev` |

示例：

```bat
curl.exe -L --get ^
  --data-urlencode "proj=warthunder" ^
  --data-urlencode "tag=" ^
  "https://yupmaster.gaijinent.com/yuitem/get_version.php"
```

测试服：

```bat
curl.exe -L --get ^
  --data-urlencode "proj=warthunder" ^
  --data-urlencode "tag=dev" ^
  "https://yupmaster.gaijinent.com/yuitem/get_version.php"
```

### 2.2 成功响应

```http
HTTP/1.1 200 OK
Content-Type: text/plain; charset=utf-8

2.59.0.98
```

响应正文就是一个四段数字版本号，没有 JSON、没有字段名。客户端应先去除首尾空白，再使用版本号比较逻辑解析。

不要按字符串直接比较，否则可能出现：

```text
"2.9.0.1" > "2.59.0.98"
```

这类错误。应把四段数字转换为 `System.Version` 或等价的数值版本对象后比较。

### 2.3 错误响应

版本接口即使出错也可能返回 HTTP `200`，因此不能只看状态码。

| 响应正文 | 含义 |
|---|---|
| `2.59.0.98` | 正常版本号 |
| `NOITEM` | 指定项目和通道没有可用版本 |
| `ERROR` | 请求参数错误，例如缺少 `proj` |

建议使用以下格式校验：

```regex
^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$
```

不匹配时，状态应视为“检查失败”，不要标记为“已是最新版”。

### 2.4 本地版本来源

PTL 按以下优先级读取当前本地版本：

1. `<游戏目录>\warthunder.yup` 中的 `yup.version`。
2. `<游戏目录>\.launcher_log\*.txt` 中的：
   - `game version: x.x.x.x`
   - `yup launcher_settings section mount: SUCCESS appVersion x.x.x.x`
3. PTL 注册表缓存中的 `LastKnownVersion`。

最可靠的数据源是 `warthunder.yup`，日志与注册表只作为回退。

---

## 3. YUP 更新清单

### 3.1 获取入口

```http
GET /yuitem/get_yup.php?proj=warthunder&tag= HTTP/1.1
Host: yupmaster.gaijinent.com
User-Agent: PTL/<launcher-version>
```

正式服保持 `tag=` 为空，测试服使用 `tag=dev`。

该入口不是返回固定的清单文件，而是先返回 `302 Found`：

```http
HTTP/1.1 302 Found
Location: https://aws-yup-distr-03.gaijinent.com/<hash>.yup
```

随后跳转到 `.yup` 文件：

```http
HTTP/1.1 200 OK
Content-Type: application/octet-stream
Accept-Ranges: bytes
```

客户端必须允许 HTTP 重定向，并限制最大重定向次数。不要把 `Location` 中的 `<hash>.yup` 当成长期固定地址，每次检查更新都应重新进入 `get_yup.php`。

建议：

- 清单流式写入临时文件，不要全部读入内存。
- PTL 当前超时为 3 分钟。
- 最终文件建议限制在 128 MiB 以内。
- 下载完成后立即解析 Bencode，确认不是 HTML 错误页。

### 3.2 无效通道

无效 `tag` 或缺少 `proj` 时，入口可能返回 HTTP `200` 和纯文本：

```text
NOITEM
```

或：

```text
ERROR
```

这两种内容都不是合法 `.yup` 文件，必须终止更新流程。

---

## 4. YUP 文件格式

### 4.1 格式性质

YUP 外层是标准 Bencode，并同时满足 BitTorrent 元信息格式。顶层实际观测字段：

```text
created by
creation date
info
scilauncher2
yup
```

可将主要结构概括如下。真实文件中，`sha1`、`pieces`、`patch` 等字段是二进制字节串，下面为了可读性用十六进制或占位符表示：

```jsonc
{
  "created by": "yupgen version 1.8.50",
  "creation date": 1793777896,

  "info": {
    "name": "a3ac8d7db91ac938899e40991898c6773b1464f3",
    "version": "2.59.0.98",
    "piece length": 1048576,
    "pieces": "<binary SHA-1 piece table>",
    "files": [
      {
        "path": ["BattlEye", "beac.zip"],
        "length": 26159727,
        "mtime": 1733489041,
        "sha1": "<20-byte SHA-1>",
        "zip": 26970192,
        "zipp": [
          "BEService.exe",
          "BEService_x64.exe"
        ]
      },
      {
        "path": [".p", "1"],
        "length": 54673,
        "attr": "p"
      }
    ]
  },

  "yup": {
    "project": "warthunder",
    "version": "2.59.0.98",
    "yup_version": 2,
    "tags": {},
    "respatch": []
  },

  "scilauncher2": "<binary launcher metadata>"
}
```

### 4.2 顶层字段

| 字段 | 类型 | 说明 |
|---|---|---|
| `created by` | byte string | 生成器标识 |
| `creation date` | integer | Unix 秒 |
| `info` | dictionary | Torrent/YUP 文件清单，计算 InfoHash 时只能使用该字典原始 Bencode 字节 |
| `scilauncher2` | byte string | 二进制启动器数据；客户端不要无修改重编码 |
| `yup` | dictionary | 战争雷霆扩展信息 |

### 4.3 `info` 字段

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | byte string | Torrent 名称；实测为 32 位十六进制字符串，也是 CDN URL 的一段 |
| `version` | byte string | 清单对应的游戏版本 |
| `piece length` | integer | Bittorrent piece 长度；实测为 `1048576` |
| `pieces` | byte string | 每 20 字节一个 SHA-1 piece 哈希 |
| `files` | list | 全部文件条目；数组顺序与 Torrent 文件索引一致 |

`info` 的 SHA-1 就是 Torrent InfoHash，也是 P2P tracker 和 DHT 使用的任务标识。

### 4.4 文件条目

| 字段 | 类型 | 说明 |
|---|---|---|
| `path` | list of byte strings | 相对路径分段，使用 `/` 连接；不得直接当成本地绝对路径 |
| `length` | integer | 最终文件长度 |
| `mtime` | integer | 文件修改时间，Unix 秒；部分条目可能没有 |
| `sha1` | 20-byte string | 文件内容 SHA-1；部分小文件或特殊条目可能没有 |
| `attr` | byte string | 文件属性；实测 `p` 表示 patch 分片 |
| `zip` | integer | ZIP 相关大小字段 |
| `zipp` | list of byte strings | 需要从该 ZIP 中释放的文件名 |

`attr=p` 的条目通常是 `.p/*` 资源补丁分片，而不是可直接覆盖的普通资源文件。PTL 当前更新计划会跳过这些条目，普通完整文件继续按路径、长度和 `sha1` 对比。

判定文件是否需要更新时，至少应检查：

1. 新清单中是否存在该路径。
2. 本地文件是否存在。
3. 本地文件长度是否等于 `length`。
4. 本地文件 SHA-1 是否等于清单中的 `sha1`。
5. 对带 `zipp` 的 ZIP，检查目标文件是否已经释放。

### 4.5 `yup` 字段

| 字段 | 类型 | 说明 |
|---|---|---|
| `project` | byte string | 固定为 `warthunder` |
| `version` | byte string | 本地 YUP 安装版本，应与线上版本一致 |
| `yup_version` | integer | YUP 格式版本；实测为 `2` |
| `tags` | dictionary | 平台、可执行文件和资源包标签 |
| `respatch` | list | 二进制资源修补记录，包含 `folders` 和 `patch` |

PTL 比较本地版本时读取 `yup.version`。`info.version` 用于更新清单本身以及 CDN 路径。

不要为了修改本地版本而重新编码整个 YUP。PTL 的做法是仅替换 `yup.version` 中长度相同的字节，并验证替换前后的 `info` InfoHash 没有变化。

---

## 5. CDN 配置接口

### 5.1 请求

```http
GET /launcher/cdn_conf.php?proj=warthunder&tag= HTTP/1.1
Host: yupmaster.gaijinent.com
User-Agent: PTL/<launcher-version>
```

正式服 `tag` 为空，测试服为 `dev`。

### 5.2 响应

响应为 `application/json`。下面是删减后的真实结构，不代表当前完整种子列表：

```json
{
  "status": "OK",
  "project": "warthunder",
  "tag": "production",
  "ip": "120.231.213.212",
  "city": "",
  "country": "CN",
  "speed_min": 5242880,
  "speed_good": 8388608,
  "silent": {
    "conn": 10,
    "speed": 2097152
  },
  "trackers": [
    "udp://yuptracker.gaijinent.com:27022/announce"
  ],
  "local_trackers": [
    "http://retracker.local/announce",
    "udp://yuptracker-sa.gaijinent.com:27022/announce"
  ],
  "seed_trackers": [
    "udp://yuptracker-sa.gaijinent.com:27029/announce"
  ],
  "hb_check": 60,
  "seeds": [
    {
      "url": "http://seed-gc-lx-01.cdn.gaijin.net/content/",
      "tier": 3,
      "https": 1
    },
    {
      "url": "http://warthunder-gcore.cdn.gaijin.net/",
      "tier": 3,
      "https": 1
    }
  ],
  "high_seeds": null
}
```

字段说明：

| 字段 | 说明 |
|---|---|
| `status` | 当前实测为字符串 `"OK"` |
| `project` | 项目名 |
| `tag` | 请求空 `tag` 时，响应一般回写 `production` |
| `ip` / `city` / `country` | 服务端识别到的客户端网络位置 |
| `speed_min` / `speed_good` | 官方启动器使用的速度阈值，单位疑似 byte/s |
| `silent` | 静默下载参数 |
| `trackers` | 主 tracker 列表 |
| `local_trackers` / `seed_trackers` | 本地或种子 tracker |
| `seeds` | HTTP Web Seed |
| `seeds[].url` | CDN 基础 URL，末尾是否带 `/` 不固定 |
| `seeds[].tier` | 优先级，当前 PTL 按降序优先使用高值 |
| `seeds[].https` | 为 `1` 时支持 HTTPS |

注意：实测给 `tag` 传无效值时，本接口仍可能返回 `"status":"OK"`，并原样回写无效 tag。不要用本接口判断通道是否存在；通道校验应以 `get_version.php` 为准。

### 5.3 CDN 文件 URL

最终文件 URL 由 CDN 基础地址、游戏名、`info.version`、`info.name` 和清单相对路径组成：

```text
<seed-base>/warthunder/<info.version>/<info.name>/<relative-path>
```

如果 `seeds[].https == 1`，应把 `http` 改为 `https`，并使用默认 HTTPS 端口。路径中的每一个分段应分别做 URL 编码，分隔符 `/` 保留。

实测样例：

```text
https://seed-gc-lx-01.cdn.gaijin.net/content/warthunder/2.59.0.98/a3ac8d7db91ac938899e40991898c6773b1464f3/aces.vromfs.bin
```

该样例返回：

```http
HTTP/1.1 200 OK
Content-Type: application/octet-stream
Content-Length: 18864017
Accept-Ranges: bytes
```

支持 `Accept-Ranges` 时，可以按字节范围断点续传：

```http
Range: bytes=<start>-<end>
```

文件下载完成后必须再次验证：

- 实际字节数等于 `length`。
- SHA-1 等于清单中的 `sha1`。
- 最终时间设置为 `mtime` 对应的 UTC 时间。

### 5.4 种子选择

建议：

1. 过滤 `url` 为空的项。
2. 按 `tier` 从高到低排序。
3. 去重。
4. 优先使用支持 HTTPS 的节点。
5. 不要硬编码本次看到的主机名，CDN 列表会按地区和发布时间变化。

PTL 当前只取排序后的前 24 个 Web Seed，并把前 8 个作为直连下载的主要候选；每个文件根据路径哈希分散到这些候选上，失败后继续尝试其他种子。

---

## 6. P2P tracker

### 6.1 tracker 地址

固定回退地址：

```text
udp://yuptracker.gaijinent.com:27022/announce
udp://yuptracker-sa.gaijinent.com:27029/announce
udp://yuptracker-sa.gaijinent.com:27022/announce
```

`cdn_conf.php` 还会返回 `trackers`、`local_trackers` 和 `seed_trackers`，客户端应与固定回退地址合并后去重。

### 6.2 协议

tracker 使用标准 UDP Tracker 协议（BEP 15）：

1. `connect`，action `0`。
2. 获取 64 位 connection ID。
3. `announce`，action `1`。
4. `info_hash` 使用 YUP 的 `info` 字典 SHA-1。
5. 返回的 peer 为 IPv4 + port，可按每 6 字节解析。

PTL 当前查询 tracker 时会请求最多 200 个 peer，连接上限为 220，Torrent 连接上限为 128。

### 6.3 Web Seed 与 P2P 混用

把配置中的 CDN 基础地址补成：

```text
<seed-base>/warthunder/<info.version>/
```

作为 Torrent HTTP Web Seed 加入任务。BitTorrent 客户端会在其后追加 `<info.name>/<relative-path>`。

推荐的下载顺序：

1. HTTP CDN 多线程直接下载缺失文件。
2. 校验失败或部分 CDN 不可用时，重试剩余文件。
3. CDN 仍未补齐时，启动 P2P，仅选择更新计划中的文件，其他文件设为不下载。
4. 所有文件通过长度和 SHA-1 校验后再安装。

---

## 7. 推荐更新流程

```text
1. 读取当前通道
   production -> tag = ""
   dev        -> tag = "dev"

2. GET get_version.php
   校验响应正文是四段数字版本号

3. 读取本地 warthunder.yup
   localVersion = yup.version
   InfoHash     = SHA1(bencoded info)

4. 比较版本
   remote <= local -> 已是最新
   remote >  local -> 继续

5. GET get_yup.php，并跟随 302
   将响应流写入临时文件

6. 解析新 YUP
   确认 info.version 不低于版本接口返回的 remote
   确认 project == warthunder

7. 生成文件更新计划
   跳过 attr=p 的 patch 分片
   对比旧/新清单
   检查本地文件是否存在、长度、SHA-1 和 ZIP 目标

8. GET cdn_conf.php
   获取 seed、HTTPS 支持标记和 tracker

9. 下载所需文件
   优先 CDN 多线程
   必要时切换 P2P 补全
   每个文件验证 length + SHA-1

10. 安装
   临时文件原子替换目标文件
   设置 mtime
   对 zipp 中的条目执行 ZIP 释放

11. 替换 warthunder.yup
   yup.version 更新到目标版本
   info 原始字节和 InfoHash 必须保持不变
   原子替换并保留旧清单用于回退

12. 清理本次版本缓存
```

### 7.1 文件计划伪代码

```text
for newFile in newManifest.info.files:
    if newFile.attr == "p":
        continue

    path = join(newFile.path, "/")
    oldFile = oldManifest.files[path]

    changed =
        oldFile is missing
        or oldFile.length != newFile.length
        or oldFile.sha1 != newFile.sha1

    localMissing = not exists(gameDir + path)
    lengthMismatch = localMissing or fileLength(gameDir + path) != newFile.length
    zipMissing = newFile.zipp is not empty and extractedTargetsMissing

    if changed or localMissing or lengthMismatch or zipMissing:
        requireDownload(newFile)
```

### 7.2 安装注意事项

- 不要先把全部更新文件缓存到另一个完整目录再统一复制；大型更新会额外占用接近一份游戏空间的磁盘。
- 可以下载到目标文件同目录的临时文件，校验通过后直接原子替换。
- 替换失败时保留临时文件，便于重试。
- 解压 `zipp` 时只允许释放清单明确列出的条目。
- 所有相对路径必须限制在游戏目录内，禁止 `..`、盘符和绝对路径。
- 新清单替换前应验证重新编码前后的 `info` InfoHash 一致。

---

## 8. 错误处理

| 场景 | 建议处理 |
|---|---|
| 版本接口返回 `NOITEM` | 视为该通道无可用版本，不标记为最新 |
| 版本接口返回 `ERROR` | 检查 `proj`、`tag` |
| HTTP 非 2xx | 视为网络或官方服务异常 |
| 重定向到非 `.yup` 内容 | 终止并删除临时文件 |
| YUP Bencode 解析失败 | 保留旧清单，终止更新 |
| `info.version < remote` | 清单异常，终止 |
| CDN 单节点失败 | 尝试其他种子，不立即终止整个任务 |
| 文件长度或 SHA-1 不符 | 删除该临时文件并重试 |
| P2P 长时间无进展 | 切回 CDN 或提示用户重试 |
| 清单替换失败 | 保留旧 `warthunder.yup`，不要报告更新成功 |

日志和 UI 不应把“网络请求失败”显示成“已是最新版”。只有明确得到有效的线上版本且 `remote <= local` 时，才能标记为最新。

---

## 9. 安全要求

1. 始终通过 HTTPS 获取版本信息和 YUP 入口。
2. CDN 支持 HTTPS 时优先使用 HTTPS。
3. 校验 `sha1` 和文件长度，不能只依赖 HTTP 状态码。
4. YUP 是外部输入，Bencode 解析器需要限制嵌套深度、字符串长度和总文件大小。
5. ZIP 和文件路径必须做 Zip Slip / Path Traversal 检查。
6. 不要把 `info.name`、文件路径或重定向目标直接拼接成未编码 URL。
7. 不要同时启动官方启动器和 PTL 更新器修改同一目录。
8. 更新期间检测 `aces`、`aces_BE` 和 `beac_wt_mlauncher` 进程，游戏运行时不要替换文件。

---

## 10. 实测记录

### 10.1 版本接口

2026-11-06 实测：

| 通道 | 参数 | 响应 |
|---|---|---|
| 正式服 | `proj=warthunder&tag=` | `2.59.0.98` |
| 测试服 | `proj=warthunder&tag=dev` | `2.60.0.34` |
| 无效通道 | `tag=does_not_exist` | `NOITEM` |
| 缺少项目 | `proj=&tag=` | `ERROR` |

### 10.2 正式服 YUP

2026-11-06 实测：

| 项目 | 值 |
|---|---|
| `info.version` | `2.59.0.98` |
| `info.name` | `a3ac8d7db91ac938899e40991898c6773b1464f3` |
| `yup.version` | `2.59.0.98` |
| `yup.project` | `warthunder` |
| `yup.yup_version` | `2` |
| `piece length` | `1048576` |
| 文件条目数 | `24049` |
| `.yup` 大小 | `9917196` 字节 |

这些值会随游戏版本变化，只用于验证解析器，不能写死到客户端。

### 10.3 字段观测

正式服样本中：

- 普通文件条目没有 `attr`。
- `.p/*` 分片通常带 `"attr": "p"`。
- 带 ZIP 释放信息的条目使用 `zipp` 数组。
- `zip` 是整数，`zipp` 才是文件名列表。
- 样本中 `attr=""` 和 `attr="p"` 的条目数量约为各一半。

解析器不要因为看到 `zip` 字段是整数就把它当作文件列表。

---

## 11. 协议参考

- BitTorrent 元信息与 Bencode：[BEP 3](https://www.bittorrent.org/beps/bep_0003.html)
- UDP Tracker 协议：[BEP 15](https://www.bittorrent.org/beps/bep_0015.html)
- HTTP Range 请求：[RFC 9110 Section 14](https://www.rfc-editor.org/rfc/rfc9110.html#section-14)

PTL 中可对照的实现位置：

- 版本检查、本地版本读取：`PTL/Modules/ModWarThunder.vb`
- YUP 入口、CDN 配置、CDN/P2P 下载：`PTL/Modules/ModWarThunderSelfUpdate.vb`
- Bencode 模型、文件条目解析、日志进度：`PTL/Modules/ModWarThunderUpdate.vb`

---

## 12. 兼容性声明

本文记录的是现有客户端逆向和实际网络请求得到的协议。Gaijin 没有承诺这些接口长期稳定，以下内容尤其容易变化：

- `get_version.php` 的文本格式和错误字符串。
- `get_yup.php` 的重定向主机。
- `cdn_conf.php` 的 JSON 字段和种子列表。
- YUP 的 `yup_version`、`tags`、`respatch` 和 ZIP 字段。
- tracker 地址、端口和返回 peer 数量。

当官方更新协议变化时，应重新抓取 `launcher.exe` / `gjagent.exe` 的请求，并以实际响应和 YUP 原始字节为准。
