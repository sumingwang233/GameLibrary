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
}

export interface CandidateItem {
  candidateId: string;
  relativePath: string;
  physicalPath: string;
  kind: string;
  reviewState: string;
  revision: number;
}

export interface TagItem {
  tagId: string;
  kind: string;
  name: string;
  color?: string | null;
  revision: number;
  gameCount?: number;
}

export interface RootItem {
  rootId: string;
  path: string;
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
  uiFontScale: number;
  uiFontFamily?: string | null;
  cacheParentDirectory?: string | null;
}

export interface LibraryEvent {
  sequence: number;
  type: string;
  entityKey?: string | null;
  timestampUtc?: string | null;
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
