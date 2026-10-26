import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { Sheet } from "./ui/sheet";

afterEach(cleanup);

function renderSheet(title: string, description?: string) {
  return render(
    <Sheet open onClose={() => {}} title={title} description={description}>
      <p>正文</p>
    </Sheet>,
  );
}

describe("Sheet 可访问名", () => {
  it("dialog 的可访问名来自 Radix 自动生成的标题 id", () => {
    renderSheet("测试游戏", "D:/Games/Test");
    const dialog = screen.getByRole("dialog", { name: "测试游戏" });
    const titleId = dialog.getAttribute("aria-labelledby");
    expect(titleId).toBeTruthy();
    expect(screen.getByRole("heading", { level: 2, name: "测试游戏" })).toHaveAttribute("id", titleId);
    expect(dialog).toHaveAccessibleDescription("D:/Games/Test");
  });

  it("没有 description 时不挂 aria-describedby", () => {
    renderSheet("只有标题");
    const dialog = screen.getByRole("dialog", { name: "只有标题" });
    expect(dialog).not.toHaveAttribute("aria-describedby");
  });
});
