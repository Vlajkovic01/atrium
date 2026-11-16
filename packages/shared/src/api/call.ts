import { toApiProblem, type ApiProblem } from "./client.ts";

/** An error the user should see: the server's code and parameters, or "network". */
export class ApiError extends Error {
  readonly problem: ApiProblem;

  constructor(problem: ApiProblem) {
    super(problem.code);
    this.problem = problem;
  }
}

interface Result<T> {
  readonly data?: T;
  readonly error?: unknown;
  readonly response: Response;
}

/**
 * Turns an API call into its data or a thrown ApiError, and a failed
 * connection into the "network" error, so every screen of every client
 * handles errors the same way.
 */
export async function call<T>(request: Promise<Result<T>>): Promise<T> {
  let result: Result<T>;
  try {
    result = await request;
  } catch {
    throw new ApiError({ code: "network", params: {}, status: 0 });
  }

  if (!result.response.ok) {
    throw new ApiError(toApiProblem(result.error, result.response.status));
  }

  return result.data as T;
}
