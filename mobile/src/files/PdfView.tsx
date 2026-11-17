import PdfRendererView from "react-native-pdf-renderer";

/**
 * A PDF kept on the phone, drawn by the system's own renderer
 * (Android's PdfRenderer, iOS's PDFKit), so the app carries no PDF engine.
 * Pages scroll and zoom; text cannot be selected and links do not work,
 * which "Open in another app" covers (D-052).
 */
export function PdfView({ uri, onError }: { readonly uri: string; readonly onError: () => void }) {
  return <PdfRendererView source={uri} distanceBetweenPages={12} maxZoom={5} onError={onError} style={{ flex: 1 }} />;
}
