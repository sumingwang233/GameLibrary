export interface Envelope<T> {
  ok: boolean;
  status: string;
  requestId: string;
  data: T;
  jobId?: string | null;
  error?: { code: string; message: string; retryable?: boolean } | null;
}

export interface GameItem {
  gameId: string;
  title: string;
  rootPath: string;
  kind: string;
  engine?: string | null;
  favorite: boolean;
  revision: number;
  coverAssetId?: string | null;
  acceptedUtc?: string;
  updatedUtc?: string;
  availability?: string;
  tags?: Array<{ tagId: string; name: string; kind?: string }>;
}

export interface CandidateItem { candidateId: string; relativePath: string; physicalPath: string; kind: string; reviewState: string; revision: number; }
export interface TagItem { tagId: string; name: string; kind?: string; gameCount?: number; }
export interface RootItem { rootId: string; path: string; revision: number; }
export interface LibrarySnapshot { games: GameItem[]; gameTotal: number; candidates: CandidateItem[]; candidateTotal: number; tags: TagItem[]; roots: RootItem[]; }
export interface TranslationPolicy { userOverride: string; inherited: string; effective: string; isRequired: boolean; revision: number; }
export interface ProfileItem { profileId: string; executablePath: string; isDefault: boolean; revision: number; toolId?: string | null; }
