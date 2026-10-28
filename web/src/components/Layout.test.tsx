import { render, screen } from "@testing-library/react";
import { Layout } from "./Layout";
import { bannerFor, describeReason } from "../lib/markings";

describe("Layout", () => {
  it("renders the page marking at the top and bottom", () => {
    render(
      <Layout banner={bannerFor("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D")} title="Part" userName="Alice"
        nav={[{ href: "#/a", label: "A", visible: true }, { href: "#/b", label: "Hidden", visible: false }]}
        current="#/a" onSignOut={() => {}}>
        <p>body</p>
      </Layout>,
    );
    const banners = screen.getAllByRole("note");
    expect(banners).toHaveLength(2);
    banners.forEach((b) => {
      expect(b).toHaveTextContent("CUI//EXPT · ITAR USML XI(d) · DISTRIBUTION D");
      expect(b).toHaveClass("tone-cui");
    });
    expect(screen.queryByText("Hidden")).not.toBeInTheDocument();
    expect(screen.getByText("A")).toHaveAttribute("aria-current", "page");
  });
});

describe("markings helpers", () => {
  it("chooses banner tone from the marking text", () => {
    expect(bannerFor("UNCLASSIFIED").tone).toBe("unclassified");
    expect(bannerFor("CUI").tone).toBe("cui");
  });
  it("explains reason codes", () => {
    expect(describeReason("cui.category_missing:CTI")).toContain("CTI");
    expect(describeReason("program.not_member")).toContain("program");
    expect(describeReason("unknown.code")).toBe("unknown.code");
  });
});
