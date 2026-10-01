import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { UnityTranslationSettingsForm, type UnityProviderInput, type UnityProviderSettings } from "./UnityTranslationSettingsForm";

afterEach(cleanup);

const settings: UnityProviderSettings = {
  provider: "deepseek", endpoint: "https://api.deepseek.com/chat/completions", model: "deepseek-flash", hasKey: true,
};

describe("Unity provider credential boundaries", () => {
  it("keeps the existing key out of the form and requires an explicit key for a different origin", async () => {
    const onSave = vi.fn(async (_input: UnityProviderInput) => undefined);
    render(<UnityTranslationSettingsForm settings={settings} onSave={onSave} onImport={vi.fn()} onCancel={vi.fn()} />);
    expect(screen.getByLabelText("API Key")).toHaveValue("");
    fireEvent.click(screen.getByRole("button", { name: "保存并继续" }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][0]).not.toHaveProperty("apiKey");
    fireEvent.change(screen.getByLabelText("翻译服务地址"), { target: { value: "https://other.example/chat/completions" } });
    fireEvent.click(screen.getByRole("button", { name: "保存并继续" }));
    expect(screen.getByRole("alert")).toHaveTextContent("需要重新填写");
    expect(onSave).toHaveBeenCalledOnce();
  });

  it("lets the first-use demo complete without calling the real save or import paths", async () => {
    const onSave = vi.fn(async () => undefined), onImport = vi.fn(async () => undefined);
    render(<UnityTranslationSettingsForm demo settings={null} onSave={onSave} onImport={onImport} onCancel={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: "填入测试密钥" }));
    fireEvent.click(screen.getByRole("button", { name: "保存并继续" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("未修改主配置"));
    expect(screen.getByLabelText("API Key")).toHaveValue("");
    expect(onSave).not.toHaveBeenCalled(); expect(onImport).not.toHaveBeenCalled();
  });
});
