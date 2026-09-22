export interface NextAction {
  operationId: string;
  actionId?: string | null;
  reason: string;
}

export interface Envelope<T> {
  apiVersion?: string;
  requestId: string;
  libraryInstanceId?: string | null;
  dataEpoch?: string | null;
  ok: boolean;
  status: string;
  data: T;
  jobId?: string | null;
  error?: { code: string; message: string; retryable?: boolean; recoveryOperation?: string | null } | null;
  warnings?: string[] | null;
  nextActions?: NextAction[] | null;
}

export interface GameTagRef {
  /** GameDto 只回传 kind+name，不含 tagId；匹配标签时用 (kind,name) 而非 tagId。 */
  kind?: string | null;
  name: string;
}

/**
 * 相似副本建议条目（candidates.accept 响应 / games.get 详情 / game.created 事件共用形状，
 * FingerprintSuggestions.SimilarGameSuggestion 经 ContractJson camelCase 落到前端）。
 */
export interface SimilarGameSuggestion {
  gameId: string;
  title: string;
  similarity: number;
}

/** games.get 附带的指纹摘要（GamesHandler.cs:165-170）；目录不可读/计算失败时后端返回 null。 */
export interface FingerprintSummary {
  strategyVersion: number;
  entryCount: number;
  computedUtc: string;
}

export interface GameItem {
  gameId: string;
  title: string;
  titleSource?: string | null;
  summary?: string | null;
  summarySource?: string | null;
  rootPath: string;
  kind: string;
  engine?: string | null;
  entryPath?: string | null;
  membership?: string | null;
  favorite: boolean;
  revision: number;
  coverAssetId?: string | null;
  acceptedUtc?: string;
  updatedUtc?: string;
  availability?: string;
  missingSinceUtc?: string | null;
  tags?: GameTagRef[];
  /** 累计游玩分钟（v1.5 feat-1，launch_attempts 聚合，GamesHandler GameDto 恒有、缺省 0）。 */
  playtimeMinutes?: number;
  /** 最近一次游玩完成时间（ISO-8601）；从未玩过为 null。 */
  lastPlayedUtc?: string | null;
  /** 仅 games.get 注入（games.list 批量 DTO 不带），旧后端无此字段。 */
  fingerprint?: FingerprintSummary | null;
  /** 仅 games.get 注入，无指纹/无命中时为空数组，旧后端无此字段。 */
  similarTo?: SimilarGameSuggestion[];
}

export interface CandidateItem {
  candidateId: string;
  relativePath: string;
  physicalPath: string;
  kind: string;
  reviewState: string;
  revision: number;
}

/**
 * candidates.accept / defer / ignore 的响应 Data（CandidateReviewHandler.CandidateReviewResult）。
 * similarTo 仅在首次 accept 建卡时计算；幂等重放（他端已 accept 同候选）返回空数组，
 * 旧后端（R60 之前）响应中无此字段——调用方须把 undefined/[] 一律当无建议。
 */
export interface CandidateReviewResult {
  reviewState: string;
  revision: number;
  gameId?: string | null;
  ignoreId?: string | null;
  similarTo?: SimilarGameSuggestion[];
}

export interface TagItem {
  tagId: string;
  kind: string;
  name: string;
  color?: string | null;
  /**
   * feat-3（迁移 v22）：四分类 engine/gameplay/social/special，缺省按 kind 推断
   * （engine→engine，user→special）。UI 展示名优先 displayName（engine 标签 name
   * 是身份键不可变，改名落 displayName）。
   */
  category?: string | null;
  sortOrder?: number;
  /** 星级评分 0–5（0=无评分）；存量布尔值 1 自然成为 1 星（迁移 v24）。 */
  starred?: number;
  displayName?: string | null;
  revision: number;
  gameCount?: number;
}

export interface RootItem {
  rootId: string;
  path: string;
  /** v23：library（扫描根）| manual（手动添加游戏的边界根，不进 roots.list/扫描）。 */
  kind?: string;
  revision: number;
}

/** views.list 同时返回内置视图与自定义视图；内置视图 revision 为 null 且不可删除。 */
export interface ViewItem {
  viewId: string;
  name: string;
  kind?: "builtin" | "custom" | string;
  search?: string | null;
  favoriteOnly?: boolean;
  sort?: string | null;
  revision: number | null;
  active?: boolean;
}

export interface NotificationItem {
  notificationId: string;
  state: string;
  candidateIds?: string[] | null;
  createdUtc?: string | null;
}

export interface IgnoreRuleItem {
  ignoreId: string;
  scope: string;
  path?: string | null;
  gameId?: string | null;
  reason?: string | null;
  revision: number;
}

export interface ProfileItem {
  profileId: string;
  gameId?: string;
  executablePath: string;
  argv?: string[];
  cwd?: string;
  isDefault: boolean;
  revision: number;
  toolId?: string | null;
}

export interface TranslationPolicy {
  userOverride: string;
  inherited: string;
  effective: string;
  isRequired: boolean;
  revision: number;
}

/** LaunchPlan.ToDto()：不含 blocked 字段——翻译路由不可用时后端抛 TranslationRouteUnavailable 错误 Envelope。 */
export interface LaunchPlan {
  planId: string;
  profileId: string;
  profileRevision?: number;
  gameId: string;
  executablePath: string;
  argv?: string[];
  cwd: string;
  createdUtc?: string;
}

export interface LibrarySettings {
  revision: number;
  activeViewId?: string | null;
  autostartEnabled: boolean;
  scanIntervalMinutes: number;
  theme: "dark" | "light" | "system";
  closeToTray: boolean;
  /** v1.5.0 下线界面缩放：uiFontScale 字段移除，前端不再读写；后端 settings 保留该字段以兼容旧库。 */
  uiFontFamily?: string | null;
  cacheParentDirectory?: string | null;
}

export interface LibraryEvent {
  sequence: number;
  type: string;
  entityKey?: string | null;
  timestampUtc?: string | null;
  /** 事件负载（如 game.created 的 {gameId,title,similarTo}）；本期前端不消费，仅保持类型完备。 */
  payload?: unknown;
}

export interface LibrarySnapshot {
  games: GameItem[];
  gameTotal: number;
  candidates: CandidateItem[];
  candidateTotal: number;
  tags: TagItem[];
  roots: RootItem[];
  views: ViewItem[];
  notifications: NotificationItem[];
}
