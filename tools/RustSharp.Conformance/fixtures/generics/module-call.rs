mod api { pub fn identity<T>(value: T) -> T { value } }
fn main() { api::identity::<i32>(1); }
