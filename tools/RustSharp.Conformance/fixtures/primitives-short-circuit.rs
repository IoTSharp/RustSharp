fn probe() -> bool { println!("probe"); true }
fn main() {
    println!("{}", false && probe());
    println!("{}", true || probe());
    println!("{}", true && probe());
}
