fn choose<T>(flag: bool, left: T, right: T) -> T { if flag { left } else { right } }
fn main() { choose(true, 1, 2); }
