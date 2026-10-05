// frozen P1 fixture: mir-projection
struct Pair(i32, i32);

fn main() {
    let pair = Pair(3, 4);
    println!("{}", pair.0 + pair.1);
}
