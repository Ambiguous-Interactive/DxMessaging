"use strict";

const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "../..");

function loadWrapper({
  python = { command: "python3", args: [] },
  result = { status: 0 },
  isSuccessImpl = (run) => run.status === 0
} = {}) {
  jest.resetModules();

  const findPython = jest.fn(() => python);
  const runCommand = jest.fn(() => result);
  const isSuccess = jest.fn(isSuccessImpl);

  jest.doMock("../ensure-pre-commit", () => ({
    findPython,
    runCommand,
    isSuccess
  }));

  let wrapper;
  jest.isolateModules(() => {
    wrapper = require("../validate-markdown-link-text");
  });

  return {
    findPython,
    isSuccess,
    runCommand,
    wrapper
  };
}

afterEach(() => {
  jest.dontMock("../ensure-pre-commit");
  jest.resetModules();
  jest.restoreAllMocks();
});

describe("validate-markdown-link-text wrapper", () => {
  test("runs the Python checker in tracked mode against the repository by default", () => {
    const { findPython, runCommand, wrapper } = loadWrapper({
      python: { command: "py", args: ["-3"] }
    });

    expect(wrapper.main([])).toBe(0);

    expect(findPython).toHaveBeenCalledWith({ runCommandFn: runCommand });
    expect(runCommand).toHaveBeenCalledWith("py", ["-3", wrapper.CHECKER, "--tracked", "."], {
      cwd: REPO_ROOT,
      stdio: "inherit",
      encoding: undefined
    });
  });

  test("passes explicit inputs after --tracked", () => {
    const { runCommand, wrapper } = loadWrapper();

    expect(wrapper.main(["README.md", ".llm"])).toBe(0);

    expect(runCommand.mock.calls[0][1]).toEqual([
      wrapper.CHECKER,
      "--tracked",
      "README.md",
      ".llm"
    ]);
  });

  test("returns 1 when Python is unavailable", () => {
    const errorSpy = jest.spyOn(console, "error").mockImplementation(() => {});
    const { runCommand, wrapper } = loadWrapper({ python: null });

    expect(wrapper.main(["README.md"])).toBe(1);

    expect(runCommand).not.toHaveBeenCalled();
    expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining("no Python 3 launcher found"));
  });

  test.each([
    ["normal non-zero status", { status: 7 }, 7],
    ["missing process status", { status: null, signal: "SIGTERM" }, 1]
  ])("maps %s to the wrapper exit code", (_name, result, expectedStatus) => {
    const { isSuccess, wrapper } = loadWrapper({
      result,
      isSuccessImpl: () => false
    });

    expect(wrapper.main(["README.md"])).toBe(expectedStatus);
    expect(isSuccess).toHaveBeenCalledWith(result);
  });
});
