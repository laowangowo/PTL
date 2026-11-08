# 战争雷霆玩家战绩查询 API

> [!IMPORTANT]
> 请勿频繁请求API，会封IP！

API：`https://api.战争雷霆.top`

---

## 1. 概述

该服务从战争雷霆助手 App 的公开资料接口获取玩家数据，在服务端完成解析、历史快照保存和头像缓存，再向 PTL 或其他客户端提供普通 JSON API。

接口分为两组：

| 分类 | 鉴权 | 用途 |
|---|---|---|
| 公共查询接口 | 无 | UID/昵称查询、缓存统计、历史快照、头像 |
| 管理接口 | `Authorization: Bearer <PTL_ADMIN_TOKEN>` | 手动刷新、导入快照、上传头像 |

客户端不需要知道 Gaijin App 的账号、密码、JWT 或请求签名。这些信息只存在于服务端环境变量和内存中。

重要限制：

- App 公共资料不提供可靠的注册时间，因此 `player.register_date` 当前固定为空字符串。
- 模式死亡字段无法稳定解析，模式级 `deaths`、`kd` 可能为 `0` 或 `null`，不应作为可靠战绩展示。
- 总 KD 使用载具明细总死亡数计算，通常比模式 KD 更可靠。
- 所有百分比字段都使用 `0-100`，不是 `0-1`。
- 时间字段为 ISO 8601 UTC 字符串。

---

## 2. 接口列表

| 方法 | 路径 | 说明 |
|---|---|---|
| `GET` | `/health` | 服务状态、缓存时间和 App 配置状态 |
| `GET` | `/api/players/{uid}/stats` | 按 UID 查询玩家战绩 |
| `GET` | `/api/players/by-nickname/{nickname}/stats` | 按昵称查询玩家战绩 |
| `GET` | `/api/players/search?nickname={nickname}` | 按昵称前缀搜索玩家 |
| `GET` | `/api/players/{uid}/snapshots?limit={limit}` | 查询历史快照元数据 |
| `GET` | `/api/players/{uid}/avatar` | 获取玩家头像文件 |
| `POST` | `/api/admin/players/{uid}/refresh` | 强制从 App 刷新战绩 |
| `POST` | `/api/admin/players/{uid}/snapshot` | 导入一份标准化战绩快照 |
| `POST` | `/api/admin/players/{uid}/avatar` | 手动上传玩家头像 |

通用请求头：

```http
Accept: application/json
User-Agent: PTL/<launcher-version>
```

服务默认允许跨域：

```http
Access-Control-Allow-Origin: *
Access-Control-Allow-Headers: Authorization,Content-Type
```

可以通过服务端环境变量 `PTL_CORS_ORIGINS` 收紧允许来源。

---

## 3. 服务状态

### 3.1 请求

```http
GET /health HTTP/1.1
Host: api.战争雷霆.top
```

### 3.2 响应

```json
{
  "status": "ok",
  "service": "ptl-node",
  "engine": "node",
  "version": "0.2.1",
  "time": "2026-11-06T13:00:19.123Z",
  "admin_token_is_default": true,
  "app_configured": true,
  "stats_cache_seconds": 600,
  "node_version": "v24.15.0"
}
```

字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `status` | string | 正常时为 `ok` |
| `service` | string | 服务名称，部署版本可能不同，不要写死判断 |
| `engine` | string | 固定为 `node` |
| `version` | string | 服务端版本 |
| `time` | string | 当前服务器时间 |
| `admin_token_is_default` | boolean | 是否仍在使用不安全的 `change-me` 管理令牌 |
| `app_configured` | boolean | 是否配置了 App 数据源账号 |
| `stats_cache_seconds` | integer | 战绩缓存有效期 |
| `node_version` | string | Node.js 版本 |

生产环境必须设置随机 `PTL_ADMIN_TOKEN`，使 `admin_token_is_default` 为 `false`。

---

## 4. 按 UID 查询战绩

### 4.1 请求

```http
GET /api/players/{uid}/stats?refresh=true&force=false HTTP/1.1
Host: api.战争雷霆.top
```

路径参数：

| 参数 | 必填 | 说明 |
|---|---|---|
| `uid` | 是 | 纯数字玩家 UID，例如 `194020182` |

查询参数：

| 参数 | 默认值 | 说明 |
|---|---|---|
| `refresh` | `true` | 只有精确值 `false` 才禁止访问 App 刷新 |
| `force` | `false` | 只有精确值 `true` 才绕过新鲜缓存 |

参数处理是区分字符串的：

```text
refresh=false  -> 不刷新
refresh=False  -> 被当作 true
force=true     -> 强制刷新
force=True     -> 被当作 false
```

示例：

```bash
curl -L \
  "https://api.战争雷霆.top/api/players/194020182/stats"
```

只读取缓存：

```bash
curl -L \
  "https://api.战争雷霆.top/api/players/194020182/stats?refresh=false"
```

强制刷新：

```bash
curl -L \
  "https://api.战争雷霆.top/api/players/194020182/stats?refresh=true&force=true"
```

Windows `cmd` 中 URL 含 `&` 时必须加双引号。

### 4.2 缓存和快照行为

服务端同时维护：

1. SQLite 中的历史快照。
2. 内存中的同一 UID 并发请求去重。
3. 头像文件缓存。

默认缓存时间：

```text
PTL_STATS_CACHE_SECONDS=600
```

| 场景 | 是否请求 App | 返回结果 |
|---|---|---|
| 10 分钟内已有 App 快照，且未 `force` | 否 | `cache_hit=true`、`stale=false` |
| 缓存过期，`refresh=true` | 是 | 新快照，`cache_hit=false`、`stale=false` |
| `refresh=false`，有缓存 | 否 | 最近快照，`stale=true` |
| `refresh=false`，无缓存 | 否 | HTTP `404` |
| `force=true`，`refresh=true` | 是 | 绕过新鲜缓存重新请求 |
| App 刷新失败，但存在旧快照 | 是，失败 | HTTP `200`，`stale=true`，并包含 `warning` |
| 同一 UID 同时收到多个请求 | 合并 | 只执行一次 App 查询 |

旧快照回退的 `warning` 示例：

```json
{
  "warning": "App refresh failed; returning the latest snapshot: App 接口 HTTP 503：upstream unavailable"
}
```

### 4.3 成功响应

完整顶层结构：

```json
{
  "player": {},
  "stats": {},
  "modes": [],
  "leaderboards": [],
  "nations": [],
  "vehicles": [],
  "snapshot": {},
  "warning": "可选"
}
```

真实数据的删减示例：

```json
{
  "player": {
    "uid": "194020182",
    "nickname": "尊界V800 第123刹车测试营",
    "level": 100,
    "exp_current": 5293855,
    "exp_to_next": 5293855,
    "level_progress": 0,
    "register_date": "",
    "clan": "=BLNKF=",
    "clan_id": "1132131",
    "title": "Cavalier of the Order of the Red Star",
    "display_title": "peacemaker",
    "avatar_icon": "cardicon_heli_ussr_modern_01",
    "avatar_url": "/api/players/194020182/avatar",
    "avatar_mirror_url": "",
    "header_url": "https://avatars.warthunder.com/header/profile_header_victory_day_2025.png",
    "frame_url": "https://avatars.warthunder.com/frame/frame_silver_figured_textured.png"
  },
  "stats": {
    "battles": 6782,
    "victories": 3062,
    "deaths": 13698,
    "air_kills": 2517,
    "ground_kills": 7597,
    "naval_kills": 86,
    "play_seconds": 2493799,
    "win_rate": 45.15,
    "kills": 10200,
    "kd": 0.74,
    "play_time": "28 天 20 小时 43 分钟"
  },
  "modes": [
    {
      "id": 0,
      "name": "街机",
      "battles": 3306,
      "victories": 1449,
      "deaths": 0,
      "win_rate": 43.83,
      "air_kills": 1574,
      "ground_kills": 4206,
      "naval_kills": 84,
      "play_seconds": 1162815,
      "play_time": "13 天 11 小时",
      "times": {
        "战斗机": {
          "seconds": 31381,
          "display": "8 小时 43 分钟"
        }
      },
      "pvp_rating": 0,
      "kd": null
    }
  ],
  "leaderboards": [
    {
      "title": "街机模式",
      "pvp_rating": 0,
      "relative_rank": 0,
      "relative_rank_recent": 0
    }
  ],
  "nations": [
    {
      "label": "苏系",
      "owned": 174,
      "elite": 46,
      "elite_rate": 26.44
    }
  ],
  "vehicles": [
    {
      "id": "ussr_t_34_1942",
      "image_url": "https://static.encyclopedia.warthunder.com/images/ussr_t_34_1942.png",
      "victories": 79,
      "battles": 203,
      "win_rate": 0,
      "deaths": 183,
      "sorties": 207,
      "air_kills": 6,
      "ground_kills": 84,
      "activity": 16626,
      "war_points": 146724
    }
  ],
  "snapshot": {
    "id": 1793967962162456,
    "uid": "194020182",
    "captured_at": "2026-11-06T12:26:02.162Z",
    "source": "app",
    "stale": false,
    "cache_hit": true
  }
}
```

> `vehicles` 实际可能包含数百项，文档示例只保留一项。客户端列表应虚拟化，避免一次创建过多 WPF 控件。

---

## 5. 响应字段

### 5.1 `player`

| 字段 | 类型 | 说明 |
|---|---|---|
| `uid` | string | 玩家数字 UID |
| `nickname` | string | 游戏昵称 |
| `level` | integer 或空字符串 | 账号等级；无法取得时为空字符串 |
| `exp_current` | integer | 当前等级经验 |
| `exp_to_next` | integer | 下一等级所需经验 |
| `level_progress` | number | App 返回的经验进度值，不建议自行假设固定范围 |
| `register_date` | string | 当前固定为空字符串 |
| `clan` | string | 战队标签，不是战队全名 |
| `clan_id` | string | 战队 ID |
| `title` | string | 当前称号资源键 |
| `display_title` | string | 展示称号资源键 |
| `avatar_icon` | string | App 头像图标键 |
| `avatar_url` | string | 头像 URL 或服务端相对路径 |
| `avatar_mirror_url` | string | Datamine 备用头像 URL，可能为空 |
| `header_url` | string | 玩家资料页横图 URL |
| `frame_url` | string | 头像框 URL |

`avatar_url` 的判断规则：

- 以 `/` 开头：相对于固定服务地址。
- 以 `http://` 或 `https://` 开头：可直接请求。
- 空字符串：没有可用头像。

不要在客户端拼接 `avatar_icon` 后绕过服务端；应优先读取 `avatar_url`，失败后再尝试 `avatar_mirror_url`。

### 5.2 `stats`

| 字段 | 类型 | 说明 |
|---|---|---|
| `battles` | integer | 总战斗数 |
| `victories` | integer | 总胜利数 |
| `deaths` | integer | 总死亡数 |
| `air_kills` | integer | 总空战击毁 |
| `ground_kills` | integer | 总地面目标击毁 |
| `naval_kills` | integer | 总海战击毁 |
| `kills` | integer | 三类击毁之和 |
| `win_rate` | number | 胜率，百分比 |
| `kd` | number 或 null | 总击毁 / 总死亡，保留两位小数 |
| `play_seconds` | integer | 总游玩秒数 |
| `play_time` | string | 中文格式化时长 |

计算规则：

```text
kills = air_kills + ground_kills + naval_kills
win_rate = victories / battles * 100
kd = kills / deaths
```

死亡数为 `0` 时，`kd` 为 `null`。机器处理时间时应使用 `play_seconds`，不要解析 `play_time`。

服务端优先使用载具明细中的死亡总数作为玩家总死亡数；载具死亡总数不可用时，才使用模式汇总。

### 5.3 `modes`

当前通常包含三个模式：

| `id` | `name` |
|---|---|
| `0` | 街机 |
| `1` | 历史 |
| `2` | 全真 |

字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | integer | 模式 ID |
| `name` | string | 中文模式名 |
| `battles` | integer | 模式战斗数 |
| `victories` | integer | 模式胜利数 |
| `deaths` | integer | 模式死亡数，可能因源数据不可靠而为 `0` |
| `win_rate` | number | 模式胜率 |
| `air_kills` | integer | 空战击毁 |
| `ground_kills` | integer | 地面击毁 |
| `naval_kills` | integer | 海战击毁 |
| `play_seconds` | integer | 模式游玩秒数 |
| `play_time` | string | 中文格式化时长 |
| `times` | object | 按载具类别拆分的游玩时间 |
| `pvp_rating` | number | App 返回的 PVP 评分；可能为 `0` 或不可靠 |
| `kd` | number 或 null | 仅在模式死亡数据可信时计算 |

`times` 中可能出现的键：

```text
重型坦克
坦克歼击车
主战坦克
自行防空
战斗机
轰炸机
攻击机
鱼雷艇
驱逐舰
```

每个值：

```json
{
  "seconds": 31381,
  "display": "8 小时 43 分钟"
}
```

> 当前 PTL 战绩页面不显示模式 KD，因为源接口的模式死亡字段不稳定。客户端也不应将模式 `kd` 作为可靠数据。

### 5.4 `leaderboards`

```json
{
  "title": "街机模式",
  "pvp_rating": 0,
  "relative_rank": 0,
  "relative_rank_recent": 0
}
```

可能出现的 `title`：

- `街机模式`
- `街机模式 · 空战`
- `街机模式 · 陆战`

这些字段直接从 App 公共资料提取。不同账号、不同版本下数值可靠性可能不同，`0` 不一定代表真实排名。

### 5.5 `nations`

```json
{
  "label": "苏系",
  "owned": 174,
  "elite": 46,
  "elite_rate": 26.44
}
```

| 字段 | 说明 |
|---|---|
| `label` | 系别 |
| `owned` | 已拥有载具数 |
| `elite` | 精英载具数 |
| `elite_rate` | 精英率百分比 |

当前映射：

```text
美系、德系、苏系、英系、日系、意系、法系、中系、瑞系、以系
```

### 5.6 `vehicles`

```json
{
  "id": "ussr_t_34_1942",
  "image_url": "https://static.encyclopedia.warthunder.com/images/ussr_t_34_1942.png",
  "victories": 79,
  "battles": 203,
  "win_rate": 0,
  "deaths": 183,
  "sorties": 207,
  "air_kills": 6,
  "ground_kills": 84,
  "activity": 16626,
  "war_points": 146724
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string | 载具内部 ID |
| `image_url` | string | 官方百科载具图片 |
| `victories` | integer | 该载具胜利数 |
| `battles` | integer | 该载具战斗数 |
| `win_rate` | number | App 返回字段；可能为 `0` |
| `deaths` | integer | 该载具死亡数 |
| `sorties` | integer | 出击数 |
| `air_kills` | integer | 空战击毁 |
| `ground_kills` | integer | 地面击毁 |
| `naval_kills` | integer | 海战击毁 |
| `activity` | integer | App 活跃度/得分指标 |
| `war_points` | integer | 战争点数 |

除 `id` 和 `image_url` 外，统计字段都可能缺失，不应假定所有载具对象字段完全一致。

### 5.7 `snapshot`

```json
{
  "id": 1793967962162456,
  "uid": "194020182",
  "captured_at": "2026-11-06T12:26:02.162Z",
  "source": "app",
  "stale": false,
  "cache_hit": true
}
```

| 字段 | 说明 |
|---|---|
| `id` | 快照数据库 ID |
| `uid` | 玩家 UID |
| `captured_at` | 抓取时间 |
| `source` | `app`、`manual import` 或管理端指定来源 |
| `stale` | 是否为旧快照回退 |
| `cache_hit` | 是否命中新鲜缓存 |

`stale=true` 时仍应展示数据，但建议标注“数据可能较旧”。

---

## 6. 按昵称查询战绩

### 6.1 请求

```http
GET /api/players/by-nickname/{nickname}/stats
```

昵称必须进行 URL 编码。

中文昵称示例：

```text
https://api.战争雷霆.top/api/players/by-nickname/%E6%B5%8B%E8%AF%95%E7%8E%A9%E5%AE%B6/stats
```

参数与 UID 查询相同：

```text
refresh=false
force=true
```

查询逻辑：

1. 先查找 SQLite 中是否已有不区分大小写的精确昵称记录。
2. 找到时直接使用该 UID。
3. 找不到时调用 App 的昵称前缀搜索。
4. 优先选择昵称完全相等的候选；没有完全匹配时选择第一个候选。
5. 查询该 UID 的战绩。

### 6.2 额外响应字段

响应是普通战绩响应的扩展：

```json
{
  "player": {},
  "stats": {},
  "modes": [],
  "leaderboards": [],
  "nations": [],
  "vehicles": [],
  "snapshot": {},
  "candidates": [
    {
      "uid": "191506144",
      "nickname": "NigelWu"
    }
  ],
  "matched_nickname": "NigelWu"
}
```

| 字段 | 说明 |
|---|---|
| `candidates` | App 返回的昵称前缀候选，最多约 10 个 |
| `matched_nickname` | 实际选中的昵称 |

客户端应核对 `matched_nickname`，避免玩家输入前缀时自动展示错误账号。

---

## 7. 搜索玩家

### 7.1 请求

```http
GET /api/players/search?nickname=NigelWu
```

参数：

| 参数 | 必填 | 说明 |
|---|---|---|
| `nickname` | 是 | 昵称或昵称前缀 |

示例：

```bash
curl -L --get \
  --data-urlencode "nickname=NigelWu" \
  "https://api.战争雷霆.top/api/players/search"
```

### 7.2 响应

```json
{
  "uid": "191506144",
  "nickname": "NigelWu",
  "candidates": [
    {
      "uid": "191506144",
      "nickname": "NigelWu"
    }
  ]
}
```

搜索最多获取约 10 个候选。顶层 `uid`、`nickname` 是服务端默认选中的候选。

该接口直接访问 App，不走战绩快照缓存，并且和战绩查询共用服务端 App 请求队列。

---

## 8. 历史快照列表

### 8.1 请求

```http
GET /api/players/{uid}/snapshots?limit=20
```

参数：

| 参数 | 默认值 | 范围 |
|---|---|---|
| `limit` | `20` | `1-100` |

示例：

```bash
curl -L \
  "https://api.战争雷霆.top/api/players/194020182/snapshots?limit=3"
```

### 8.2 响应

```json
[
  {
    "id": 1793967962162456,
    "uid": "194020182",
    "captured_at": "2026-11-06T12:26:02.162Z",
    "source": "app"
  },
  {
    "id": 1793471029188447,
    "uid": "194020182",
    "captured_at": "2026-10-31T18:23:49.188Z",
    "source": "app"
  }
]
```

接口只返回快照元数据，不返回快照正文。结果按 `captured_at` 和 `id` 倒序排列。

当前 PTL 战绩页面已移除历史快照入口，但服务端接口仍然保留。

---

## 9. 玩家头像

### 9.1 请求

```http
GET /api/players/{uid}/avatar
```

### 9.2 成功响应

返回图片二进制，不是 JSON：

```http
HTTP/1.1 200 OK
Content-Type: image/png
Cache-Control: public, max-age=21600
```

浏览器/CDN 缓存时间：

```text
21600 秒 = 6 小时
```

### 9.3 404 响应

```json
{
  "detail": "Avatar is not available.",
  "reason": "App profile has no icon or all configured avatar sources failed."
}
```

常见原因：

- App 资料中没有 `avatar_icon`。
- Gaijin 官方头像 CDN 返回 403 或其他错误。
- Datamine 镜像也没有对应图标。
- 没有历史快照，服务端无法重新尝试缓存头像。
- 缓存文件损坏或已被删除。

头像获取顺序：

1. 已缓存的本地头像。
2. 快照中的 `avatar_url`。
3. `avatar_mirror_url`。
4. 根据 `avatar_icon` 构造的官方 CDN URL。
5. 根据 `avatar_icon` 构造的 War Thunder Datamine 镜像 URL。

服务端只缓存 PNG、JPEG、WebP，单个头像最大 8 MiB。

---

## 10. 管理接口

所有管理接口必须携带：

```http
Authorization: Bearer <PTL_ADMIN_TOKEN>
```

令牌无效时：

```http
HTTP/1.1 401 Unauthorized
```

```json
{
  "detail": "Administrator token is missing or invalid."
}
```

### 10.1 强制刷新玩家

```http
POST /api/admin/players/{uid}/refresh HTTP/1.1
Authorization: Bearer <token>
```

该接口无视缓存，直接请求 App 并创建新快照。响应结构与普通战绩查询相同。

### 10.2 导入标准化快照

```http
POST /api/admin/players/{uid}/snapshot HTTP/1.1
Authorization: Bearer <token>
Content-Type: application/json
```

请求体：

```json
{
  "source": "manual import",
  "player": {
    "uid": "194020182",
    "nickname": "PlayerName"
  },
  "stats": {
    "battles": 100,
    "victories": 55,
    "deaths": 80,
    "air_kills": 10,
    "ground_kills": 90,
    "naval_kills": 0,
    "play_seconds": 360000
  },
  "modes": [],
  "leaderboards": [],
  "nations": [],
  "vehicles": []
}
```

要求：

- `player` 必须是对象。
- `stats` 必须是对象。
- 四个数组字段缺省时自动视为空数组。
- `player.uid` 缺省时使用路径中的 UID。

响应在普通战绩结构后增加：

```json
{
  "created_snapshot": {
    "id": 1794000000000000,
    "uid": "194020182",
    "captured_at": "2026-11-06T13:10:00.000Z",
    "source": "manual import"
  }
}
```

### 10.3 上传玩家头像

```http
POST /api/admin/players/{uid}/avatar HTTP/1.1
Authorization: Bearer <token>
Content-Type: multipart/form-data
```

表单字段：

| 字段 | 类型 | 说明 |
|---|---|---|
| `file` | file | PNG、JPEG 或 WebP，最大 8 MiB |

示例：

```bash
curl -X POST \
  -H "Authorization: Bearer <token>" \
  -F "file=@avatar.png" \
  "https://api.战争雷霆.top/api/admin/players/194020182/avatar"
```

响应：

```json
{
  "status": "ok",
  "avatar_url": "/api/players/194020182/avatar"
}
```

---

## 11. 错误格式

公共错误统一返回 JSON：

```json
{
  "detail": "错误说明"
}
```

| HTTP 状态 | 场景 |
|---|---|
| `400` | UID 不是纯数字、头像格式错误、快照请求体缺少 `player/stats` |
| `401` | 管理令牌缺失或错误 |
| `404` | 无缓存快照、头像不存在、API 路径不存在 |
| `413` | 上传文件过大 |
| `500` | 未预期的服务端异常 |
| `503` | App 未配置、Gaijin 登录失败、App 接口错误、玩家不存在 |

典型错误：

```json
{ "detail": "玩家 UID 必须是数字。" }
```

```json
{ "detail": "No cached snapshot is available." }
```

```json
{ "detail": "App 数据源未配置，请设置 WT_APP_LOGIN 和 WT_APP_PASSWORD。" }
```

```json
{ "detail": "App 接口没有找到该玩家。" }
```

---

## 12. 客户端调用建议

### 12.1 按 UID 查询

```javascript
const BASE_URL = "https://api.战争雷霆.top";

async function getPlayerStats(uid, { refresh = true, force = false } = {}) {
  const url = new URL(
    `/api/players/${encodeURIComponent(uid)}/stats`,
    BASE_URL
  );
  url.searchParams.set("refresh", String(refresh));
  if (force) url.searchParams.set("force", "true");

  const response = await fetch(url, {
    headers: { Accept: "application/json" }
  });
  const data = await response.json();
  if (!response.ok) throw new Error(data.detail || `HTTP ${response.status}`);
  return data;
}
```

### 12.2 头像解析

```javascript
function resolveServiceUrl(value) {
  if (!value) return "";
  return value.startsWith("/") ? BASE_URL + value : value;
}
```

### 12.3 请求策略

- 页面首次打开可以调用普通查询。
- 10 分钟内再次进入同一玩家页面，建议使用 `refresh=false`。
- 用户点击“刷新战绩”时使用 `force=true`。
- 不要高频轮询；服务端只有一个共享 App 账号，查询会串行排队。
- 展示总 KD 时使用 `stats.kd`。
- 展示时长时优先格式化 `play_seconds`。
- `snapshot.stale=true` 时提示“使用缓存数据”。
- 昵称查询必须检查 `matched_nickname` 和 `candidates`。

---

## 13. 服务端内部的 App 数据源

这一节仅用于维护服务端。普通客户端不得直接调用这些接口，也不应持有 App 账号。

### 13.1 App 登录

```http
POST https://auth.gaijinent.com/login.php
Content-Type: multipart/form-data
User-Agent: wtr-assistant/235/1.10.3 (Android 10; MIX 2S)
```

表单字段：

| 字段 | 说明 |
|---|---|
| `login` | Gaijin 账号 |
| `password` | 密码 |
| `factor` | 随机设备因子 |
| `v` | 固定为 `2` |
| `jwt_public_key` | 服务端临时 RSA 公钥 PEM |
| `lang` | `en` |
| `client` | 与 `factor` 相同 |

成功响应包含 JWT。JWT、账号和密码只保存在服务端进程内或环境变量中。

### 13.2 签名请求

```http
POST https://companion-app.warthunder.com/call_signed/
Authorization: Bearer <jwt>
request-sign: <signature>
Content-Type: application/json; charset=utf-8
```

请求正文是 JSON，响应是 Protobuf 二进制。

昵称搜索：

```json
{
  "classname": "eaw_Contacts",
  "method": "jzx_findUsersByNickPrefix",
  "v": 8,
  "nick": "PlayerName",
  "count": 10,
  "factor": "<device-factor>"
}
```

玩家公开资料：

```json
{
  "classname": "eaw_Profile",
  "method": "jzx_getPublic",
  "v": 8,
  "userid": "194020182",
  "lang": "en",
  "factor": "<device-factor>"
}
```

### 13.3 头像来源

官方头像：

```text
https://public-configs.gaijin.net/gaijinPass/avatars/<avatar_icon>.png
```

Datamine 镜像：

```text
https://raw.githubusercontent.com/gszabi99/War-Thunder-Datamine/master/
images.vromfs.bin_u/images/avatars/<avatar_icon>.png
```

---

## 14. 服务端环境变量

| 变量 | 默认值 | 说明 |
|---|---|---|
| `WT_APP_LOGIN` | 空 | App 查询账号 |
| `WT_APP_PASSWORD` | 空 | App 查询密码 |
| `WT_APP_TIMEOUT_MS` | `30000` | App 请求超时 |
| `PTL_STATS_CACHE_SECONDS` | `600` | 战绩缓存秒数 |
| `PTL_ADMIN_TOKEN` | `change-me` | 管理接口令牌 |
| `PTL_DATA_DIR` | `server/data` | SQLite、头像和资源目录 |
| `PTL_CORS_ORIGINS` | `*` | 允许跨域的来源 |
| `WT_AVATAR_MIRROR_TEMPLATE` | 内置 Datamine URL | 自定义头像镜像模板 |
| `HOST` | `0.0.0.0` | 监听地址 |
| `PORT` | `8787` | 监听端口 |

生产环境要求：

1. 使用专用 Gaijin 查询账号。
2. 不要把账号和密码提交到 Git。
3. 设置随机长 `PTL_ADMIN_TOKEN`。
4. 限制 `PTL_CORS_ORIGINS`。
5. 备份 `server/data`。

---

## 15. 数据来源与兼容性

服务端实现参考：

- [HSOS6/astrbot_plugin_warthunder_stats](https://github.com/HSOS6/astrbot_plugin_warthunder_stats)
- War Thunder Assistant App 的公开资料接口
- War Thunder Datamine 头像镜像

兼容性风险：

- App 登录和请求签名没有公开稳定协议。
- Protobuf 字段编号可能变化。
- Gaijin 官方头像 CDN 可能拒绝请求。
- 模式死亡、PVP rating、载具胜率等字段存在源数据可靠性问题。
- 注册时间目前不可获取。
- 服务端版本与接口字段变化时，应以实际 JSON 和 `server/app.js` 为准。

