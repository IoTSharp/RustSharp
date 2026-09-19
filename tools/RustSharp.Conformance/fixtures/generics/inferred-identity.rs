fn identity<T>(value: T) -> T { value }
fn main() { let x: i32 = identity(1); let flag: bool = identity(true); }
