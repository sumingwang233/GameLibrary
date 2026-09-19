import { LayoutGrid, List, RefreshCw, Search } from "lucide-react";
import { Button } from "./ui/button";
import { Input } from "./ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "./ui/select";

/** 后端 QueryGames 的 ORDER BY 白名单，多传的值会落到默认分支（名称升序）。 */
export const SORT_OPTIONS: Array<{ value: string; label: string }> = [
  { value: "accepted-desc", label: "最近入库" },
  { value: "updated-desc", label: "最近修改" },
  { value: "title-asc", label: "名称 A→Z" },
  { value: "title-desc", label: "名称 Z→A" },
];

export function LibraryToolbar({
  search,
  onSearch,
  sort,
  onSort,
  onRefresh,
  layout,
  onLayout,
  total,
  shown,
}: {
  search: string;
  onSearch: (value: string) => void;
  sort: string;
  onSort: (value: string) => void;
  onRefresh: () => void;
  layout: "grid" | "compact";
  onLayout: (value: "grid" | "compact") => void;
  total: number;
  shown: number;
}) {
  return (
    <div className="mb-5 flex flex-wrap items-center gap-3">
      <div className="relative min-w-[220px] flex-1">
        <Search
          size={16}
          aria-hidden="true"
          className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-text-secondary"
        />
        <Input
          type="search"
          value={search}
          onChange={(event) => onSearch(event.currentTarget.value)}
          placeholder="搜索游戏标题或路径"
          className="pl-9"
          aria-label="搜索游戏"
        />
      </div>

      <div className="flex items-center gap-2">
        <span className="text-xs whitespace-nowrap text-text-secondary" aria-live="polite">
          {shown.toLocaleString()} / {total.toLocaleString()}
        </span>
        <Select value={sort} onValueChange={onSort}>
          <SelectTrigger className="w-[130px]" aria-label="排序方式">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {SORT_OPTIONS.map((option) => (
              <SelectItem key={option.value} value={option.value}>
                {option.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>

        <Button variant="outline" size="icon" onClick={onRefresh} aria-label="刷新">
          <RefreshCw size={16} />
        </Button>

        <div className="flex rounded-md border border-border p-1">
          <Button
            variant={layout === "grid" ? "default" : "ghost"}
            size="icon"
            onClick={() => onLayout("grid")}
            aria-label="网格视图"
            aria-pressed={layout === "grid"}
          >
            <LayoutGrid size={16} />
          </Button>
          <Button
            variant={layout === "compact" ? "default" : "ghost"}
            size="icon"
            onClick={() => onLayout("compact")}
            aria-label="紧凑视图"
            aria-pressed={layout === "compact"}
          >
            <List size={16} />
          </Button>
        </div>
      </div>
    </div>
  );
}
