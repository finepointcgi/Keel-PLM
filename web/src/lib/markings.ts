export type BannerTone = "unclassified" | "cui";

/** A page must declare the highest marking of the data it shows (ADR-0010). */
export interface PageBanner {
  text: string;
  tone: BannerTone;
}

export const NoControlledData: PageBanner = { text: "UNCLASSIFIED", tone: "unclassified" };

export function bannerFor(text: string): PageBanner {
  return { text, tone: text.startsWith("CUI") ? "cui" : "unclassified" };
}

const reasonText: Record<string, string> = {
  "role.missing": "Your roles do not permit this action.",
  "export.us_person_or_authorization_required":
    "Export-controlled: requires U.S.-person status or an export authorization listed on the object.",
  "program.not_member": "You are not a member of the object's program.",
  "org.not_owner": "Only the owning organization or program members may modify this object.",
};

export function describeReason(code: string): string {
  if (code.startsWith("cui.category_missing:")) return `You are not authorized for CUI category ${code.split(":")[1]}.`;
  return reasonText[code] ?? code;
}
