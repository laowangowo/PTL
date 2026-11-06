# WT Live API 文档（瞄具 SIGHTS / 音效包 SOUNDS）

> 适用站点：`https://live.warthunder.com/`
>
> 文档范围：瞄具（Sights）和音效包（Sounds）的公开列表、搜索、作者浏览、详情、
> 预览图与附件下载。
>
> 实测日期：2026-11-06
>
> 参考文档：[WTLive 涂装 API](WTLive_涂装_API.md)
>
> 性质说明：WT Live 没有面向第三方的稳定公开 API 合同。本文基于站点页面、
> `main.js` 和匿名 HTTP 请求反向整理；接口、参数和字段可能随站点改版而变化。
>
---

## 1. 核心结论

- **SIGHTS 和 SOUNDS 共用同一套 API。** 列表、作者浏览、详情和下载都复用涂装的
  Feed/Post 协议，通过 `content` 和响应中的 `type` 区分分类。
- **页面路径使用复数，API 枚举使用单数。**
- **列表、搜索、详情、预览图和附件下载均可匿名访问。**
- **下载直链已经内嵌在列表响应中**，不需要为下载再请求一次详情。
- **`get_regular` 能可靠地按分类过滤。** 传入正确的单数 `content` 时，实测两页
  25/25 条均为目标类型。
- **不能依赖 HTTP 状态码判断业务成功。** 接口通常返回 HTTP 200，应检查响应体
  顶层 `status == "OK"`。
- **音效包附件可能非常大。** 实测当前第一页最大文件约 888 MB，必须使用流式下载、
  磁盘空间检查、停滞超时和断点/重试能力。

---

## 2. 分类映射

| 用户界面分类 | 页面路径 | `content` 参数 | 响应中的 `type` |
|---|---|---|---|
| 瞄具 / SIGHTS | `/feed/sights/` | `sight` | `sight` |
| 音效包 / SOUNDS | `/feed/sounds/` | `sound` | `sound` |

必须将以下三个值视为独立概念：

```text
页面 slug   = sights / sounds
API content = sight  / sound
响应 type   = sight  / sound
```

### 2.1 单数枚举是关键约束

当前服务端不会因为传入复数而返回明确的参数错误：

```text
content=sights
content=sounds
```

这两种写法都仍然返回 HTTP 200 和 `status: "OK"`，但结果是未正确按分类过滤的
混合 feed。实测 `content=sights` 的首页同时出现了 `camouflage`、`image`、
`mission` 和 `sight`，页面标题还被回写为错误的 `Sightss`。

客户端处理规则：

1. 请求时只发送 `sight` 或 `sound`。
2. 解析列表时再次校验 `item.type`。
3. 丢弃类型不匹配的记录，不要只依赖服务端过滤。

---

## 3. 通用传输约定

### 3.1 基地址

```text
https://live.warthunder.com
```

### 3.2 请求方法

列表和详情均使用 `POST`，数据编码为表单：

```text
application/x-www-form-urlencoded; charset=UTF-8
```

### 3.3 推荐请求头

```http
User-Agent: <常规浏览器 UA>
X-Requested-With: XMLHttpRequest
Referer: https://live.warthunder.com/feed/sights/
```

音效包请求建议使用：

```http
Referer: https://live.warthunder.com/feed/sounds/
```

### 3.4 鉴权

本文覆盖的公开读取能力均不要求登录：

- 分类列表
- 标签搜索
- 指定作者公开主页
- 帖子详情
- 图片
- 附件下载

发布、编辑、点赞、评论和“我的订阅/隐藏内容”属于另一套需要登录的流程，不在本文
的公开浏览范围内。

---

## 4. 列表 API

### 4.1 端点

```http
POST https://live.warthunder.com/api/feed/get_regular/
```

### 4.2 请求参数

| 参数 | 必填 | 取值 | 说明 |
|---|---|---|---|
| `content` | 是 | `sight` / `sound` | 分类，严格使用单数 |
| `sort` | 否 | `created` / `rating` / `comments` / `downloads` | 排序方式 |
| `page` | 是 | 从 0 开始的整数 | 页码，每页 25 条 |
| `period` | 否 | `0` / `1` / `7` / `30` / `365` | 时间范围，`0` 表示全部 |
| `subtype` | 否 | `all` | 当前分类没有额外子类型 |
| `searchString` | 否 | 标签字符串，如 `#anime` | 标签搜索 |
| `user` | 否 | `0` | 对 `get_regular` 无效 |
| `vehicle` | 否 | 载具裸 ID | 已确认对涂装有效；瞄具/音效包未验证 |

`sort=created` 返回发布时间倒序。客户端应显式发送 `sort`、`page`、`period`，
不要依赖站点默认值。

### 4.3 瞄具请求示例

```bash
curl -X POST "https://live.warthunder.com/api/feed/get_regular/" \
  -H "User-Agent: Mozilla/5.0" \
  -H "X-Requested-With: XMLHttpRequest" \
  -H "Referer: https://live.warthunder.com/feed/sights/" \
  --data-urlencode "content=sight" \
  --data-urlencode "sort=created" \
  --data-urlencode "page=0" \
  --data-urlencode "period=0" \
  --data-urlencode "subtype=all" \
  --data-urlencode "searchString=" \
  --data-urlencode "user=0"
```

### 4.4 音效包请求示例

```bash
curl -X POST "https://live.warthunder.com/api/feed/get_regular/" \
  -H "User-Agent: Mozilla/5.0" \
  -H "X-Requested-With: XMLHttpRequest" \
  -H "Referer: https://live.warthunder.com/feed/sounds/" \
  --data-urlencode "content=sound" \
  --data-urlencode "sort=created" \
  --data-urlencode "page=0" \
  --data-urlencode "period=0" \
  --data-urlencode "subtype=all" \
  --data-urlencode "searchString=" \
  --data-urlencode "user=0"
```

### 4.5 响应结构

```json
{
  "status": "OK",
  "data": {
    "list": [],
    "pageTitle": "WT Live // Sights",
    "link": "/feed/sights/"
  }
}
```

成功时：

- HTTP 状态通常为 `200`
- 顶层 `status` 为字符串 `"OK"`
- `data.list` 为帖子数组
- `data.pageTitle` 为当前分类/筛选的页面标题
- `data.link` 为当前筛选对应的站点路径

失败时应优先看响应体中的 `status` 和 `msg`，不能仅依赖 HTTP 状态。

### 4.6 列表字段

瞄具和音效包的列表记录与涂装基本同构。当前实测字段如下：

| 字段 | 类型 | 说明 |
|---|---|---|
| `lang_group` | int | 帖子稳定定位 ID |
| `id` | int | 当前语言版本的内部 ID |
| `language` | string | 语言码，如 `en`、`ru` |
| `type` | string | `sight` 或 `sound` |
| `created` | int | Unix 秒 |
| `author` | object | `{id,nickname,avatar}` |
| `likes` | int | 点赞数 |
| `views` | int | 浏览数 |
| `downloads` | int | 下载数 |
| `comments` | int | 评论数 |
| `description` | string | 含 HTML 的正文 |
| `images` | array | 列表预览图 |
| `file` | object/null | 附件信息；可能为空 |
| `visible` | bool | 对当前访客是否可见 |
| `featured` | bool | 是否精选 |
| `isPinned` | bool | 是否置顶 |
| `pbr_ready` | bool | Feed 通用字段，对瞄具/音效包无业务意义 |
| `inverted_roughness` | bool | Feed 通用字段，对瞄具/音效包无业务意义 |

不要因为 `pbr_ready == false` 就认定瞄具或音效包无效；这是涂装/PBR 流程遗留的通用
字段。

### 4.7 分页

- `page` 从 `0` 开始。
- 固定 25 条/页。
- 若返回不足 25 条，可视为当前筛选下已到末页。
- 实测 `page=0` 和 `page=1` 均为 25 条，且没有重复 `lang_group`。
- 翻页时建议按 `lang_group` 去重。

公开 feed 建议在翻页之间增加少量延迟，并处理临时网络错误。

---

## 5. 标签搜索

标签搜索仍通过 `get_regular` 的 `searchString` 完成：

```text
content=sight&searchString=%23anime
content=sound&searchString=%23anime
```

`#anime` 在表单编码后可写作 `%23anime`。

延续涂装接口的实测结论：

- 只支持标签搜索，不支持普通全文搜索。
- 裸词如 `anime` 与标签 `#anime` 不等价。
- 多标签以空格分隔，服务端按并集处理。
- 标签大小写不敏感。
- 标签字符集以字母、数字和下划线为主，连字符可能无法匹配。

本次对 `sight` 和 `sound` 分别使用 `searchString=#anime`，均返回 25/25 条目标类型，
分类过滤正常。

客户端应把多个标签归一化为单个空格分隔，并保留前导 `#`。

---

## 6. 按作者浏览

### 6.1 端点

```http
POST https://live.warthunder.com/api/feed/get_user/
```

参数与 `get_regular` 相同，但必须提供有效作者 ID：

```text
user=<author.id>
```

该端点匿名可用，返回结构与 `get_regular` 相同。

### 6.2 重要限制：作者流可能混合内容类型

2026-11-06 对两个有混合作品的作者进行实测：

- 瞄具请求中传入了 `content=sight`，返回结果包含 24 条 `sight` 和 1 条
  `camouflage`。
- 音效包请求中传入了 `content=sound`，返回结果包含 10 条 `sound` 和 3 条
  `quote`。

因此作者流不能作为严格的分类过滤接口。客户端必须在收到结果后再次执行：

```text
accept if item.type == expectedType
```

并按 `lang_group` 去重。

---

## 7. 帖子详情

### 7.1 端点

```http
POST https://live.warthunder.com/api/posts/get/
```

### 7.2 请求参数

| 参数 | 必填 | 来源 |
|---|---|---|
| `lang_group` | 是 | 列表记录的 `lang_group` |
| `language` | 是 | 列表记录的 `language` |

示例：

```bash
curl -X POST "https://live.warthunder.com/api/posts/get/" \
  -H "User-Agent: Mozilla/5.0" \
  -H "X-Requested-With: XMLHttpRequest" \
  -H "Referer: https://live.warthunder.com/post/1194155/en/" \
  --data-urlencode "lang_group=1194155" \
  --data-urlencode "language=en"
```

不要将列表记录里的内部 `id` 当作 `lang_group` 使用。

### 7.3 响应

成功响应仍具有：

```json
{
  "status": "OK",
  "data": {
    "type": "sight"
  }
}
```

详情会在列表字段的基础上增加：

| 字段 | 说明 |
|---|---|
| `languages` | 该帖可用的语言版本数组 |
| `hidden` | 隐藏状态 |
| `pageTitle` | 帖子页面标题 |
| `gamePreviewAvailable` | 是否提供站点游戏内预览能力；实测样本为 `false` |
| `images[].mq` | 中等清晰度图片，含 `src/width/height/ratio` |
| `images[].orig` | 原图，至少含 `src` |

### 7.4 `Content-Type` 异常

当前 `/api/posts/get/` 的响应头为：

```text
Content-Type: text/html; charset=UTF-8
```

但响应体实际是可解析的 JSON。客户端应以实际响应体和顶层 `status` 为准，
不要因为响应头不是 `application/json` 就判定失败。

---

## 8. 图片与预览

### 8.1 列表图片

列表中的 `images` 是数组，每一项当前形态为：

```json
{
  "id": 2699631,
  "type": "image/png",
  "src": "https://cdn-live.warthunder.com/..._lq/...png",
  "width": 386,
  "height": 217,
  "ratio": 1.78
}
```

列表 URL 带 `_lq`，适合卡片缩略图。

### 8.2 详情图片

详情接口的每个图片项包含 `mq` 和 `orig`：

- `orig.src`：原图
- `mq.src`：中等清晰度图
- 列表 `_lq` 路径可按涂装文档的规则改写为 `_mq` 或去掉后缀取得更清晰版本

实际使用时应保留 404 或解码失败时回退到列表 `src` 的逻辑。

### 8.3 预览能力边界

- 瞄具 API 没有单独的准星预览接口；`images` 是作者上传的截图。
- 音效包 API 没有音频试听流；实际试听通常需要下载 ZIP 并在本地解析。
- `gamePreviewAvailable` 不是附件或音频 URL，不应作为媒体地址使用。

---

## 9. 附件与下载

### 9.1 附件对象

列表和详情中的 `file` 当前形态为：

```json
{
  "id": 2699633,
  "name": "Neo.zip",
  "link": "https://live.warthunder.com/dl/<hash>/",
  "type": "application/zip",
  "size": 35408
}
```

字段说明：

- `name`：文件名和扩展名
- `link`：匿名可用的下载入口
- `type`：MIME 类型
- `size`：字节数

瞄具和音效包当前抽样均为 ZIP，但客户端仍应以 `file.name`、`file.type` 和实际内容
校验，不要只根据分类硬编码格式。

### 9.2 下载请求

```http
GET file.link
User-Agent: <常规浏览器 UA>
Referer: https://live.warthunder.com/
```

请求路径通常先返回：

```text
302 Found
Location: https://cdn-live.warthunder.com/uploads/.../file.zip
```

CDN 最终响应：

```text
Content-Type: application/octet-stream
Accept-Ranges: bytes
```

客户端必须跟随重定向。当前响应没有 `Content-Disposition`，保存文件名应来自
`file.name`，并进行非法字符和路径穿越清理。

### 9.3 体量特征

2026-11-06 对各自第一页 25 条的统计：

| 分类 | 文件格式 | 第一页总大小 | 中位数 | 最大值 |
|---|---:|---:|---:|---:|
| 瞄具 | 25/25 ZIP | 约 4.2 MB | 约 84 KB | 约 1.9 MB |
| 音效包 | 25/25 ZIP | 约 4.59 GB | 约 35.5 MB | 约 888 MB |

这些数字仅用于说明量级，不应写死到业务逻辑。

### 9.4 下载实现要求

音效包是下载器的主要风险来源：

- 使用流式响应写入磁盘，不要一次性读入内存。
- 下载前检查剩余磁盘空间。
- 不做整体下载总超时；使用“无字节接收时长”的停滞超时。
- 支持失败重试和尽可能的 Range 断点续传。
- 显示已下载字节、总字节和速度。
- 下载完成后校验 ZIP 条目路径，拒绝越界路径、嵌套压缩包和可执行文件。
- 解压前再次估算所需空间。
- 以 `lang_group + file.id/hash` 作为本地缓存键更稳妥。

瞄具文件通常较小，但仍应使用同一套安全校验。

---

## 10. 错误和兼容性处理

### 10.1 请求结果判定

建议顺序：

1. 检查网络/HTTP 传输是否成功。
2. 尝试解析响应体。
3. 检查顶层 `status` 是否为 `"OK"`。
4. 检查 `data.list` 或 `data.type` 是否符合预期。
5. 对每条记录执行 `item.type` 白名单过滤。

### 10.2 常见边界

| 场景 | 处理 |
|---|---|
| `content=sights/sounds` | 禁止发送；结果可能混合类型 |
| 顶部 `status != "OK"` | 读取 `msg`，不要继续展示旧请求结果 |
| `file == null` | 不显示可下载按钮，或引导到帖子页面 |
| `images` 为空 | 显示占位图 |
| 作者流出现其他 `type` | 客户端过滤 |
| 详情响应头为 `text/html` | 仍按 JSON 解析响应体 |
| 翻页出现重复记录 | 按 `lang_group` 去重 |
| CDN 下载失败 | 针对单文件重试；保留已完成分段 |

---

## 11. 发布/上传接口

站点还存在需要登录的作者侧接口。它们是反向整理结果，本文没有完成带账号的端到端
发布验证，不建议作为稳定公开 API 使用。

从当前 `main.js` 可确认：

| 用途 | 瞄具 SIGHTS | 音效包 SOUNDS |
|---|---|---|
| 显示发布表单 | `POST /api/post_add/show_form/` | 同左 |
| 上传归档 | `/api/upload/archive/` | `/api/upload/sound/` |
| 上传图片 | `/api/upload/image/` | 同左 |
| 最终提交 | `POST /api/post_add/add/` | 同左 |

瞄具没有单独的 `/api/upload/sight/` 分支，而是使用通用
`/api/upload/archive/`。

当前前端提示：

- 瞄具和音效包附件只接受 ZIP。
- 不允许压缩包内再嵌套压缩包。
- 不允许可执行文件。

上述限制来自前端逻辑，服务端最终校验规则仍需登录后单独测试。

---

## 12. 推荐客户端模型

### 12.1 分类描述符

```csharp
public sealed class WtLiveCategory
{
    public string PageSlug { get; }
    public string Content { get; }
    public string ExpectedType { get; }

    public WtLiveCategory(
        string pageSlug,
        string content,
        string expectedType)
    {
        PageSlug = pageSlug;
        Content = content;
        ExpectedType = expectedType;
    }
}

public static readonly WtLiveCategory Sights =
    new("sights", "sight", "sight");

public static readonly WtLiveCategory Sounds =
    new("sounds", "sound", "sound");
```

### 12.2 共享服务

`WtLiveFeedClient` 可同时服务涂装、瞄具和音效包：

1. 用分类描述符生成 `content` 和 `Referer`。
2. 始终发送 `sort=created`、明确的 `period` 和从 0 开始的 `page`。
3. 按 `item.type == category.ExpectedType` 过滤。
4. 以 `lang_group` 去重和标识帖子。
5. 使用 `language` 请求详情。
6. 直接使用 `file.link` 下载。

业务层可以分开：

- 瞄具：小文件、图片预览、ZIP 内继续解析准星资源。
- 音效包：大文件、持久化下载队列、可恢复下载、ZIP 资源解析。

### 12.3 建议的最小数据对象

```csharp
public sealed class WtLivePost
{
    public long LangGroup { get; set; }
    public long Id { get; set; }
    public string Language { get; set; } = "";
    public string Type { get; set; } = "";
    public long Created { get; set; }
    public WtLiveAuthor Author { get; set; } = new WtLiveAuthor();
    public string DescriptionHtml { get; set; } = "";
    public List<WtLiveImage> Images { get; set; } = new List<WtLiveImage>();
    public WtLiveFile File { get; set; }
    public int Likes { get; set; }
    public int Views { get; set; }
    public int Downloads { get; set; }
    public int Comments { get; set; }
}
```

---

## 13. 实测矩阵

| 项目 | SIGHTS | SOUNDS |
|---|---|---|
| `/api/feed/get_regular/` | HTTP 200，`OK` | HTTP 200，`OK` |
| 正确 `content` | 25/25 为 `sight` | 25/25 为 `sound` |
| 第 0/1 页 | 均 25 条，无交叠 | 均 25 条，无交叠 |
| `searchString=#anime` | 25/25 为 `sight` | 25/25 为 `sound` |
| `get_user` 匿名 | HTTP 200 | HTTP 200 |
| `get_user` 类型过滤 | 混入 `camouflage` | 混入 `quote` |
| `/api/posts/get/` | 可解析 JSON | 可解析 JSON |
| 详情响应头 | `text/html; charset=UTF-8` | `text/html; charset=UTF-8` |
| `/dl/<hash>/` | 匿名 302 → CDN | 匿名 302 → CDN |
| Range 支持 | CDN 返回 `Accept-Ranges: bytes` | CDN 返回 `Accept-Ranges: bytes` |

---

## 14. 相关页面与端点

页面：

- `https://live.warthunder.com/feed/sights/`
- `https://live.warthunder.com/feed/sounds/`

公开读取端点：

```text
POST /api/feed/get_regular/
POST /api/feed/get_user/
POST /api/posts/get/
GET  /dl/<hash>/
```

相关但不属于本文核心公开读取范围：

```text
POST /api/post_add/show_form/
POST /api/upload/archive/
POST /api/upload/sound/
POST /api/upload/image/
POST /api/post_add/add/
```

---

## 15. 变更风险

本文基于 2026-11-06 的站点行为。WT Live 的接口没有公开版本合同，以下内容最容易
发生变化：

- `content`/`type` 枚举
- 详情响应头
- 图片清晰度路径规则
- 上传字段和文件限制
- CDN 重定向及 Range 行为
- 匿名访问策略

上线前建议至少复核一次：

1. 两个分类首页是否仍严格返回目标 `type`。
2. 详情接口是否仍返回 JSON 响应体。
3. `file.link` 是否仍可匿名下载。
4. 音效包的大文件是否仍支持 Range。
