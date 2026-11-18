import { createElement } from "react";

// Only for looking at the app's screens in a browser while developing
// (mobile/preview): the browser's own PDF viewer stands in for the phone's.
export function PdfView({ uri }: { readonly uri: string; readonly onError: () => void }) {
  return createElement("iframe", {
    src: uri,
    title: "PDF",
    style: { flex: 1, border: 0, width: "100%", height: "100%" },
  });
}
