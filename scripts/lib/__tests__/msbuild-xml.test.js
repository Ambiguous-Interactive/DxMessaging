/**
 * @fileoverview Fixture set for scripts/lib/msbuild-xml.js -- the
 * formatting-invariant readers for MSBuild / structured XML config. These cover
 * the exact shapes that broke the old hand-rolled assertions: a wrapped closing
 * tag, a multi-attribute open tag split one attribute per line, attribute
 * reordering, CRLF content, and self-closing elements. This file is the SINGLE
 * allowlisted exception in xml-contract-assertion-policy.test.js because it is
 * the helper's own test and legitimately contains XML literals.
 */

"use strict";

const {
  normalizeNewlines,
  stripComments,
  getPropertyValue,
  resolveProperty,
  hasElement,
  getElements,
  parseAttributes
} = require("../msbuild-xml");

describe("getPropertyValue", () => {
  test("single-line element", () => {
    expect(getPropertyValue("<P><LangVersion>10.0</LangVersion></P>", "LangVersion")).toBe("10.0");
  });

  test("element with attributes on the open tag", () => {
    const xml = `<X Condition="'$(A)' == ''">value</X>`;
    expect(getPropertyValue(xml, "X")).toBe("value");
  });

  test("wrapped closing tag '</X\\n  >'", () => {
    const xml =
      "<ArtifactsRoot\n      >$(SolutionDir).artifacts/$(MSBuildProjectName)/</ArtifactsRoot\n    >";
    expect(getPropertyValue(xml, "ArtifactsRoot")).toBe(
      "$(SolutionDir).artifacts/$(MSBuildProjectName)/"
    );
  });

  test("open tag's '>' on its own line", () => {
    const xml =
      "<AnalyzerPayloadOutputDir Condition=\"'$(X)' == ''\"\n  >Editor/Analyzers</AnalyzerPayloadOutputDir\n  >";
    expect(getPropertyValue(xml, "AnalyzerPayloadOutputDir")).toBe("Editor/Analyzers");
  });

  test("CRLF content", () => {
    const xml =
      "<Project>\r\n  <PropertyGroup>\r\n    <DebugType>none</DebugType>\r\n  </PropertyGroup>\r\n</Project>\r\n";
    expect(getPropertyValue(xml, "DebugType")).toBe("none");
  });

  test("word boundary: <Foo does not match <FooBar", () => {
    const xml = "<FooBar>x</FooBar><Foo>y</Foo>";
    expect(getPropertyValue(xml, "Foo")).toBe("y");
  });

  test("missing element returns null", () => {
    expect(getPropertyValue("<P><A>1</A></P>", "Missing")).toBeNull();
  });

  test("self-closing element returns null (no inner text)", () => {
    expect(getPropertyValue('<P><Item Include="x" /></P>', "Item")).toBeNull();
  });

  test("skips a leading self-closing same-named element and reads the later valued one", () => {
    // The first <Foo .../> is self-closing; the reader must not bail to null but
    // continue to the second <Foo>...</Foo> that carries the value.
    expect(getPropertyValue('<Foo Condition="x" /><Foo>realvalue</Foo>', "Foo")).toBe("realvalue");
  });

  test("ignores a commented-out element and reads the real one after it", () => {
    const xml =
      "<!-- <LangVersion>latest</LangVersion> --><PropertyGroup><LangVersion>10.0</LangVersion></PropertyGroup>";
    expect(getPropertyValue(xml, "LangVersion")).toBe("10.0");
  });

  test("returns null when the ONLY occurrence is inside a comment", () => {
    expect(getPropertyValue("<P><!-- <DebugType>full</DebugType> --></P>", "DebugType")).toBeNull();
  });

  test("multi-line comment containing an element is ignored", () => {
    const xml = [
      "<Project>",
      "  <!--",
      "    <DebugType>full</DebugType>",
      "  -->",
      "  <PropertyGroup>",
      "    <DebugType>none</DebugType>",
      "  </PropertyGroup>",
      "</Project>"
    ].join("\n");
    expect(getPropertyValue(xml, "DebugType")).toBe("none");
  });

  test("an element name appearing inside another element's quoted attribute value is not matched", () => {
    // A stray `<Foo>` inside a quoted attribute value of an earlier tag must not
    // be mistaken for the start of a `Foo` element. The real `<Foo>v</Foo>` wins.
    const xml = '<Other Note="<Foo>"/><Foo>v</Foo>';
    expect(getPropertyValue(xml, "Foo")).toBe("v");
  });
});

describe("resolveProperty", () => {
  const props = [
    "<Project>",
    "  <PropertyGroup>",
    "    <ArtifactsRoot Condition=\"'$(ArtifactsRoot)' == ''\"",
    "      >$(SolutionDir).artifacts/$(MSBuildProjectName)/</ArtifactsRoot",
    "    >",
    "    <BaseIntermediateOutputPath>$(ArtifactsRoot)obj/</BaseIntermediateOutputPath>",
    "    <OutputPath>$(ArtifactsRoot)bin/$(Configuration)/</OutputPath>",
    "    <Unresolved>$(NotDeclaredAnywhere)/tail</Unresolved>",
    "  </PropertyGroup>",
    "</Project>"
  ].join("\r\n");

  test("nested ref resolves transitively under .artifacts with default seeds", () => {
    const resolved = resolveProperty(props, "OutputPath");
    expect(resolved).toBe("REPO/.artifacts/ProjName/bin/Release/");
  });

  test("seed override changes the per-project subtree", () => {
    expect(resolveProperty(props, "OutputPath", { MSBuildProjectName: "ProjA" })).toContain(
      "ProjA"
    );
    expect(resolveProperty(props, "OutputPath", { MSBuildProjectName: "ProjB" })).toContain(
      "ProjB"
    );
  });

  test("unresolved reference is left as a literal $(name)", () => {
    expect(resolveProperty(props, "Unresolved")).toBe("$(NotDeclaredAnywhere)/tail");
  });

  test("absent property resolves to null", () => {
    expect(resolveProperty(props, "DoesNotExist")).toBeNull();
  });

  test("cycle is broken by the stack guard", () => {
    const cyclic = "<P><A>$(B)</A><B>$(A)</B></P>";
    expect(resolveProperty(cyclic, "A")).toBe("");
  });

  test("repeated reference to the same property expands at EVERY occurrence", () => {
    const xml = "<P><B>x</B><A>$(B)-$(B)</A></P>";
    expect(resolveProperty(xml, "A")).toBe("x-x");
  });

  test("diamond reference (two paths to the same property) fully resolves", () => {
    const xml = [
      "<Project><PropertyGroup>",
      "<ArtifactsRoot>$(SolutionDir).artifacts/</ArtifactsRoot>",
      "<Both>$(ArtifactsRoot)obj/ and $(ArtifactsRoot)bin/</Both>",
      "</PropertyGroup></Project>"
    ].join("");
    expect(resolveProperty(xml, "Both")).toBe("REPO/.artifacts/obj/ and REPO/.artifacts/bin/");
  });
});

describe("hasElement", () => {
  test("single-line multi-attribute open tag", () => {
    const xml = '<Target Name="PostBuildCopyAnalyzers" AfterTargets="Build">x</Target>';
    expect(
      hasElement(xml, {
        name: "Target",
        attributes: { Name: "PostBuildCopyAnalyzers", AfterTargets: "Build" }
      })
    ).toBe(true);
  });

  test("multi-attribute open tag split one attribute per line", () => {
    const xml = [
      "<Target",
      '  Name="PostBuildCopyAnalyzers"',
      '  AfterTargets="Build"',
      "  Condition=\"'$(CopyAnalyzerPayload)' == 'true'\"",
      ">",
      "</Target>"
    ].join("\n");
    expect(
      hasElement(xml, {
        name: "Target",
        attributes: {
          Name: "PostBuildCopyAnalyzers",
          AfterTargets: "Build",
          Condition: "'$(CopyAnalyzerPayload)' == 'true'"
        }
      })
    ).toBe(true);
  });

  test("attribute reordering still matches", () => {
    const xml = '<PackageReference Version="3.8.0" Include="Microsoft.CodeAnalysis.CSharp">';
    expect(
      hasElement(xml, {
        name: "PackageReference",
        attributes: { Include: "Microsoft.CodeAnalysis.CSharp", Version: "3.8.0" }
      })
    ).toBe(true);
  });

  test("CRLF content", () => {
    const xml =
      '<ItemGroup>\r\n  <PackageReference Include="X" Version="1.0">\r\n  </PackageReference>\r\n</ItemGroup>';
    expect(
      hasElement(xml, { name: "PackageReference", attributes: { Include: "X", Version: "1.0" } })
    ).toBe(true);
  });

  test("self-closing element matches", () => {
    const xml = '<InternalsVisibleTo Include="Tests" />';
    expect(hasElement(xml, { name: "InternalsVisibleTo", attributes: { Include: "Tests" } })).toBe(
      true
    );
  });

  test("single quotes inside a double-quoted condition do not confuse the scanner", () => {
    const xml = "<Target Name=\"T\" Condition=\"'$(X)' == 'true'\">body</Target>";
    expect(hasElement(xml, { name: "Target", attributes: { Condition: "'$(X)' == 'true'" } })).toBe(
      true
    );
  });

  test("wrong attribute value does not match", () => {
    const xml = '<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.0.0">';
    expect(
      hasElement(xml, {
        name: "PackageReference",
        attributes: { Include: "Microsoft.CodeAnalysis.CSharp", Version: "3.8.0" }
      })
    ).toBe(false);
  });

  test("skips a non-matching element and finds a later matching one", () => {
    const xml = [
      '<PackageReference Include="Other" Version="1.0" />',
      '<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="3.8.0">',
      "  <PrivateAssets>all</PrivateAssets>",
      "</PackageReference>"
    ].join("\n");
    expect(
      hasElement(xml, {
        name: "PackageReference",
        attributes: { Include: "Microsoft.CodeAnalysis.CSharp", Version: "3.8.0" }
      })
    ).toBe(true);
  });

  test("missing element returns false", () => {
    expect(hasElement("<P></P>", { name: "Target", attributes: { Name: "x" } })).toBe(false);
  });

  test("does NOT match an element that exists only inside a comment", () => {
    const xml =
      '<!-- <Target Name="PostBuildCopyAnalyzers" AfterTargets="Build">stale</Target> --><Project></Project>';
    expect(
      hasElement(xml, {
        name: "Target",
        attributes: { Name: "PostBuildCopyAnalyzers", AfterTargets: "Build" }
      })
    ).toBe(false);
  });

  test("matches a real element even when a commented copy precedes it", () => {
    const xml = [
      '<!-- <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.0.0" /> -->',
      '<PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="3.8.0" />'
    ].join("\n");
    expect(
      hasElement(xml, {
        name: "PackageReference",
        attributes: { Include: "Microsoft.CodeAnalysis.CSharp", Version: "3.8.0" }
      })
    ).toBe(true);
    // And the commented-out 4.0.0 copy must NOT register as a match.
    expect(
      hasElement(xml, {
        name: "PackageReference",
        attributes: { Include: "Microsoft.CodeAnalysis.CSharp", Version: "4.0.0" }
      })
    ).toBe(false);
  });
});

describe("getElements", () => {
  test("enumerates every same-named element with parsed attributes in document order", () => {
    const xml = [
      "<Project>",
      "  <PropertyGroup Condition=\"'$(X)' == 'a'\">",
      "  </PropertyGroup>",
      "  <PropertyGroup>",
      "  </PropertyGroup>",
      "  <PropertyGroup",
      "    Condition=\"'$(X)' == 'b'\"",
      "  >",
      "  </PropertyGroup>",
      "</Project>"
    ].join("\n");
    const groups = getElements(xml, "PropertyGroup");
    expect(groups).toHaveLength(3);
    expect(groups[0].attributes.Condition).toBe("'$(X)' == 'a'");
    expect(groups[1].attributes.Condition).toBeUndefined();
    expect(groups[2].attributes.Condition).toBe("'$(X)' == 'b'");
  });

  test("ignores elements that exist only inside a comment", () => {
    const xml = '<!-- <PropertyGroup Condition="x" /> --><PropertyGroup></PropertyGroup>';
    expect(getElements(xml, "PropertyGroup")).toHaveLength(1);
  });

  test("returns an empty array for an absent element", () => {
    expect(getElements("<P></P>", "Nope")).toEqual([]);
  });

  test("exposes each element's trimmed inner value (and null for self-closing)", () => {
    const xml = [
      "<Project>",
      "  <MicrosoftCodeAnalysisVersion>3.8.0</MicrosoftCodeAnalysisVersion>",
      "  <MicrosoftCodeAnalysisVersion",
      "    >4.0.0</MicrosoftCodeAnalysisVersion",
      "  >",
      '  <Item Include="x" />',
      "</Project>"
    ].join("\n");
    const versions = getElements(xml, "MicrosoftCodeAnalysisVersion");
    expect(versions.map((element) => element.value)).toEqual(["3.8.0", "4.0.0"]);
    const items = getElements(xml, "Item");
    expect(items).toHaveLength(1);
    expect(items[0].selfClosing).toBe(true);
    expect(items[0].value).toBeNull();
  });
});

describe("parseAttributes / normalizeNewlines", () => {
  test("parseAttributes returns name/value pairs tolerant of mixed quotes", () => {
    expect(parseAttributes("<X A=\"1\" B='2' C=\"'q'\">")).toEqual({ A: "1", B: "2", C: "'q'" });
  });

  test("normalizeNewlines collapses CRLF and lone CR to LF", () => {
    expect(normalizeNewlines("a\r\nb\rc\nd")).toBe("a\nb\nc\nd");
  });

  test("stripComments blanks comment bodies while preserving length and newlines", () => {
    const input = "a<!-- x\ny -->b";
    const output = stripComments(input);
    expect(output).toHaveLength(input.length);
    // Newlines inside the comment are preserved so line/offset math stays aligned.
    expect(output).toBe("a      \n     b");
    expect(output).not.toMatch(/x|y/);
  });

  test("stripComments blanks an unterminated comment to end-of-input", () => {
    const input = "ok<!-- dangling";
    const output = stripComments(input);
    expect(output).toHaveLength(input.length);
    expect(output).toBe("ok" + " ".repeat(input.length - 2));
  });
});
