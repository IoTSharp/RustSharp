fn identity<T>(value: T) -> T { value }
fn main() { identity::<i32>(1); identity::<bool>(true); }
