import { call } from "../api/call.ts";
import type { ApiClient } from "../api/client.ts";
import type { components } from "../api/schema.d.ts";

export type SessionInfo = components["schemas"]["SessionInfo"];
export type PairingDetails = components["schemas"]["PairingDetails"];

/** What a person allows a device without a keyboard (a TV) when approving it (DESIGN.md 6.5). */
export interface PairingApproval {
  /** It may look and play, never change or delete. */
  readonly readOnly: boolean;
  /** The person's own files stay out of sight on it, for a shared screen. */
  readonly hidePersonalLibrary: boolean;
}

/** The person's signed-in devices, and approving a device that shows a code. */
export function devicesApi(api: ApiClient) {
  return {
    sessions: (): Promise<SessionInfo[]> => call(api.GET("/api/v1/sessions")),
    /** Signs a device out on the server: its next request finds the session gone. */
    signOut: async (id: string): Promise<void> => {
      await call(api.DELETE("/api/v1/sessions/{id}", { params: { path: { id } } }));
    },
    /** The device waiting with this code, to show before approving. */
    pairing: (userCode: string): Promise<PairingDetails> =>
      call(api.GET("/api/v1/auth/pairing/{userCode}", { params: { path: { userCode } } })),
    approve: async (userCode: string, approval: PairingApproval): Promise<void> => {
      await call(
        api.POST("/api/v1/auth/pairing/{userCode}/approve", {
          params: { path: { userCode } },
          body: { readOnly: approval.readOnly, hidePersonalLibrary: approval.hidePersonalLibrary },
        }),
      );
    },
    deny: async (userCode: string): Promise<void> => {
      await call(api.POST("/api/v1/auth/pairing/{userCode}/deny", { params: { path: { userCode } } }));
    },
  };
}

export type DevicesApi = ReturnType<typeof devicesApi>;

/**
 * A code as typed or read from a TV's QR code, in the form the server
 * keeps: "ABCD-1234". Null when it cannot be one.
 */
export function normalizePairingCode(text: string): string | null {
  const plain = text.trim().toUpperCase().replace(/[\s-]/g, "");
  return /^[A-Z0-9]{8}$/.test(plain) ? `${plain.slice(0, 4)}-${plain.slice(4)}` : null;
}
