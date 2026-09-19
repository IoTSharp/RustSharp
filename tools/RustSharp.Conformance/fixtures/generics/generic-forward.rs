fn identity<T>(value: T) -> T { value }
fn forward<T>(value: T) -> T { let result: T = identity(value); result }
fn main() { forward(1); }
