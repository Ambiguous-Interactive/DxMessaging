"use strict";

const { scanContent } = require("../check-conflict-markers");

describe("check-conflict-markers", () => {
  test("reports standard merge marker lines", () => {
    const violations = scanContent(
      "example.txt",
      ["ok", "<<<<<<< HEAD", "mine", "=======", "theirs", ">>>>>>> branch"].join("\n")
    );

    expect(violations.map((violation) => violation.line)).toEqual([2, 4, 6]);
  });

  test("does not flag marker text in the middle of a line", () => {
    const violations = scanContent(
      "example.txt",
      ["Use <<<<<<< as literal documentation.", "value === other"].join("\n")
    );

    expect(violations).toEqual([]);
  });
});
